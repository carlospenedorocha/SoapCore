using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoapCore.Extensibility;
using SoapCore.MessageEncoder;
using SoapCore.Meta;
using SoapCore.ServiceModel;

namespace SoapCore
{
	public class SoapEndpointMiddleware<T_MESSAGE>
		where T_MESSAGE : CustomMessage, new()
	{
		private readonly ILogger<SoapEndpointMiddleware<T_MESSAGE>> _logger;
		private readonly RequestDelegate _next;
		private readonly SoapOptions _options;
		private readonly ServiceDescription _service;
		private readonly StringComparison _pathComparisonStrategy;
		private readonly SoapMessageEncoder[] _messageEncoders;
		private readonly SerializerHelper _serializerHelper;

		[Obsolete]
		public SoapEndpointMiddleware(ILogger<SoapEndpointMiddleware<T_MESSAGE>> logger, RequestDelegate next, Type serviceType, string path, SoapEncoderOptions[] encoderOptions, SoapSerializer serializer, bool caseInsensitivePath, ISoapModelBounder soapModelBounder, Binding binding, bool httpGetEnabled, bool httpsGetEnabled)
			: this(logger, next, new SoapOptions()
			{
				ServiceType = serviceType,
				Path = path,
				EncoderOptions = encoderOptions ?? binding?.ToEncoderOptions(),
				SoapSerializer = serializer,
				CaseInsensitivePath = caseInsensitivePath,
				SoapModelBounder = soapModelBounder,
				UseBasicAuthentication = binding.HasBasicAuth(),
				HttpGetEnabled = httpGetEnabled,
				HttpsGetEnabled = httpsGetEnabled
			})
		{
		}

		public SoapEndpointMiddleware(ILogger<SoapEndpointMiddleware<T_MESSAGE>> logger, RequestDelegate next, SoapOptions options)
		{
			_logger = logger;
			_next = next;
			_options = options;

			_serializerHelper = new SerializerHelper(options.SoapSerializer);
			_pathComparisonStrategy = options.CaseInsensitivePath ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			_service = new ServiceDescription(options.ServiceType);

			if (options.EncoderOptions is null)
			{
				options.EncoderOptions = new[] { new SoapEncoderOptions() };
			}

			_messageEncoders = new SoapMessageEncoder[options.EncoderOptions.Length];

			for (var i = 0; i < options.EncoderOptions.Length; i++)
			{
				_messageEncoders[i] = new SoapMessageEncoder(options.EncoderOptions[i].MessageVersion, options.EncoderOptions[i].WriteEncoding, options.EncoderOptions[i].ReaderQuotas, options.OmitXmlDeclaration, options.IndentXml);
			}
		}

		public async Task Invoke(HttpContext httpContext, IServiceProvider serviceProvider)
		{
			var trailPathTuner = serviceProvider.GetService<TrailingServicePathTuner>();

			trailPathTuner?.ConvertPath(httpContext);

			if (httpContext.Request.Path.Equals(_options.Path, _pathComparisonStrategy))
			{
				if (httpContext.Request.Method?.ToLower() == "get")
				{
					// If GET is not enabled, either for HTTP or HTTPS, return a 403 instead of the WSDL
					if ((httpContext.Request.IsHttps && !_options.HttpsGetEnabled) || (!httpContext.Request.IsHttps && !_options.HttpGetEnabled))
					{
						httpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
						return;
					}
				}

				try
				{
					_logger.LogDebug("Received SOAP Request for {0} ({1} bytes)", httpContext.Request.Path, httpContext.Request.ContentLength ?? 0);

					if ((string.IsNullOrEmpty(httpContext.Request.ContentType) || httpContext.Request.Query.ContainsKey("wsdl")) && httpContext.Request.Method?.ToLower() == "get")
					{
						if (_options.WsdlFileOptions != null)
						{
							await ProcessMetaFromFile(httpContext);
						}
						else
						{
							await ProcessMeta(httpContext);
						}
					}
					else if (httpContext.Request.Query.ContainsKey("xsd") && httpContext.Request.Method?.ToLower() == "get" && _options.WsdlFileOptions != null)
					{
						await ProcessXSD(httpContext);
					}
					else
					{
						await ProcessOperation(httpContext, serviceProvider);
					}
				}
				catch (Exception ex)
				{
					_logger.LogCritical(ex, "An error occurred when trying to service a request on SOAP endpoint: {0}", httpContext.Request.Path);

					// Let's pass this up the middleware chain after we have logged this issue
					// and signaled the critical of it
					throw;
				}
			}
			else
			{
				await _next(httpContext);
			}
		}

#if !NETCOREAPP3_0_OR_GREATER
		private static Task WriteMessageAsync(SoapMessageEncoder messageEncoder, Message responseMessage, HttpContext httpContext)
		{
			return messageEncoder.WriteMessageAsync(responseMessage, httpContext.Response.Body);
		}

		private static Task<Message> ReadMessageAsync(HttpContext httpContext, SoapMessageEncoder messageEncoder)
		{
			return messageEncoder.ReadMessageAsync(httpContext.Request.Body, 0x10000, httpContext.Request.ContentType);
		}
#else
		private static Task WriteMessageAsync(SoapMessageEncoder messageEncoder, Message responseMessage, HttpContext httpContext)
		{
			return messageEncoder.WriteMessageAsync(responseMessage, httpContext.Response.BodyWriter);
		}

		private static Task<Message> ReadMessageAsync(HttpContext httpContext, SoapMessageEncoder messageEncoder)
		{
			return messageEncoder.ReadMessageAsync(httpContext.Request.BodyReader, 0x10000, httpContext.Request.ContentType);
		}
#endif

		private async Task ProcessMeta(HttpContext httpContext)
		{
			var baseUrl = httpContext.Request.Scheme + "://" + httpContext.Request.Host + httpContext.Request.PathBase + httpContext.Request.Path;
			var xmlNamespaceManager = GetXmlNamespaceManager();
			var bindingName = "BasicHttpBinding";

			var bodyWriter = _options.SoapSerializer == SoapSerializer.XmlSerializer
				? new MetaBodyWriter(_service, baseUrl, xmlNamespaceManager, bindingName, _messageEncoders[0].MessageVersion)
				: (BodyWriter)new MetaWCFBodyWriter(_service, baseUrl, bindingName, _options.UseBasicAuthentication);

			using var responseMessage = new MetaMessage(
				Message.CreateMessage(_messageEncoders[0].MessageVersion, null, bodyWriter),
				_service,
				xmlNamespaceManager,
				bindingName,
				_options.UseBasicAuthentication);

			//we should use text/xml in wsdl page for browser compability.
			httpContext.Response.ContentType = "text/xml;charset=UTF-8"; // _messageEncoders[0].ContentType;

			await WriteMessageAsync(_messageEncoders[0], responseMessage, httpContext);
		}

		private async Task ProcessOperation(HttpContext httpContext, IServiceProvider serviceProvider)
		{
			Message responseMessage;

			// Get the encoder based on Content Type
			var messageEncoder = _messageEncoders[0];

			foreach (var encoder in _messageEncoders)
			{
				if (encoder.IsContentTypeSupported(httpContext.Request.ContentType))
				{
					messageEncoder = encoder;
					break;
				}
			}

			Message requestMessage;

			//Get the message
			try
			{
				requestMessage = await ReadMessageAsync(httpContext, messageEncoder);
			}
			catch (Exception ex)
			{
				await WriteErrorResponseMessage(ex, StatusCodes.Status500InternalServerError, serviceProvider, null, messageEncoder, httpContext);
				return;
			}

			var asyncMessageFilters = serviceProvider.GetServices<IAsyncMessageFilter>().ToArray();

			//Execute request message filters
			try
			{
				foreach (var messageFilter in asyncMessageFilters)
				{
					await messageFilter.OnRequestExecuting(requestMessage);
				}
			}
			catch (Exception ex)
			{
				await WriteErrorResponseMessage(ex, StatusCodes.Status500InternalServerError, serviceProvider, requestMessage, messageEncoder, httpContext);
				return;
			}

			var soapAction = HeadersHelper.GetSoapAction(httpContext, ref requestMessage);
			requestMessage.Headers.Action = soapAction;

			var messageInspector2s = serviceProvider.GetServices<IMessageInspector2>();
			var correlationObjects2 = default(List<(IMessageInspector2 inspector, object correlationObject)>);

			try
			{
				correlationObjects2 = messageInspector2s.Select(mi => (inspector: mi, correlationObject: mi.AfterReceiveRequest(ref requestMessage, _service))).ToList();
			}
			catch (Exception ex)
			{
				await WriteErrorResponseMessage(ex, StatusCodes.Status500InternalServerError, serviceProvider, requestMessage, messageEncoder, httpContext);
				return;
			}

			// for getting soapaction and parameters in (optional) body
			// GetReaderAtBodyContents must not be called twice in one request
			XmlDictionaryReader reader = null;
			if (!requestMessage.IsEmpty)
			{
				reader = requestMessage.GetReaderAtBodyContents();
			}

			try
			{
				var operation = _service.Operations.FirstOrDefault(o => o.SoapAction.Equals(soapAction, StringComparison.Ordinal)
				|| o.Name.Equals(HeadersHelper.GetTrimmedSoapAction(soapAction), StringComparison.Ordinal)
				|| soapAction.Equals(HeadersHelper.GetTrimmedSoapAction(o.Name), StringComparison.Ordinal));

				if (operation == null)
				{
					operation = _service.Operations.FirstOrDefault(o => soapAction.Equals(HeadersHelper.GetTrimmedClearedSoapAction(o.SoapAction), StringComparison.Ordinal));
				}

				if (operation == null)
				{
					var ex = new InvalidOperationException($"No operation found for specified action: {requestMessage.Headers.Action}");
					await WriteErrorResponseMessage(ex, StatusCodes.Status500InternalServerError, serviceProvider, requestMessage, messageEncoder, httpContext);
					return;
				}

				_logger.LogInformation("Request for operation {0}.{1} received", operation.Contract.Name, operation.Name);

				try
				{
					//Create an instance of the service class
					var serviceInstance = serviceProvider.GetRequiredService(_service.ServiceType);

					SetMessageHeadersToProperty(requestMessage, serviceInstance);

					// Get operation arguments from message
					var arguments = GetRequestArguments(requestMessage, reader, operation, httpContext);

					ExecuteFiltersAndTune(httpContext, serviceProvider, operation, arguments, serviceInstance);

					var invoker = serviceProvider.GetService<IOperationInvoker>() ?? new DefaultOperationInvoker();
					var responseObject = await invoker.InvokeAsync(operation.DispatchMethod, serviceInstance, arguments);

					if (operation.IsOneWay)
					{
						httpContext.Response.StatusCode = (int)HttpStatusCode.Accepted;
						return;
					}

					var resultOutDictionary = new Dictionary<string, object>();
					foreach (var parameterInfo in operation.OutParameters)
					{
						resultOutDictionary[parameterInfo.Name] = arguments[parameterInfo.Index];
					}

					responseMessage = CreateResponseMessage(
						operation, responseObject, resultOutDictionary, soapAction, requestMessage, messageEncoder);

					httpContext.Response.ContentType = httpContext.Request.ContentType;
					httpContext.Response.Headers["SOAPAction"] = responseMessage.Headers.Action;

					correlationObjects2.ForEach(mi => mi.inspector.BeforeSendReply(ref responseMessage, _service, mi.correlationObject));

					SetHttpResponse(httpContext, responseMessage);

					await WriteMessageAsync(messageEncoder, responseMessage, httpContext);
				}
				catch (Exception exception)
				{
					if (exception is TargetInvocationException targetInvocationException)
					{
						exception = targetInvocationException.InnerException;
					}

					responseMessage = await WriteErrorResponseMessage(exception, StatusCodes.Status500InternalServerError, serviceProvider, requestMessage, messageEncoder, httpContext);
				}
			}
			finally
			{
				reader?.Dispose();
			}

			// Execute response message filters
			try
			{
				foreach (var messageFilter in asyncMessageFilters.Reverse())
				{
					await messageFilter.OnResponseExecuting(responseMessage);
				}
			}
			catch (Exception ex)
			{
				responseMessage = await WriteErrorResponseMessage(ex, StatusCodes.Status500InternalServerError, serviceProvider, requestMessage, messageEncoder, httpContext);
			}
		}

		private Message CreateResponseMessage(
			OperationDescription operation,
			object responseObject,
			Dictionary<string, object> resultOutDictionary,
			string soapAction,
			Message requestMessage,
			SoapMessageEncoder soapMessageEncoder)
		{
			T_MESSAGE responseMessage;

			// Create response message
			var bodyWriter = new ServiceBodyWriter(_options.SoapSerializer, operation, responseObject, resultOutDictionary);
			var xmlNamespaceManager = GetXmlNamespaceManager();

			if (soapMessageEncoder.MessageVersion.Addressing == AddressingVersion.WSAddressing10)
			{
				responseMessage = new T_MESSAGE
				{
					Message = Message.CreateMessage(soapMessageEncoder.MessageVersion, soapAction, bodyWriter),
					NamespaceManager = xmlNamespaceManager
				};
				responseMessage.Headers.Action = operation.ReplyAction;
				responseMessage.Headers.RelatesTo = requestMessage.Headers.MessageId;
				responseMessage.Headers.To = requestMessage.Headers.ReplyTo?.Uri;
			}
			else
			{
				responseMessage = new T_MESSAGE
				{
					Message = Message.CreateMessage(soapMessageEncoder.MessageVersion, null, bodyWriter),
					NamespaceManager = xmlNamespaceManager
				};
			}

			if (responseObject != null)
			{
				var messageHeaderMembers = responseObject.GetType().GetMembersWithAttribute<MessageHeaderAttribute>();
				foreach (var messageHeaderMember in messageHeaderMembers)
				{
					var messageHeaderAttribute = messageHeaderMember.GetCustomAttribute<MessageHeaderAttribute>();
					responseMessage.Headers.Add(MessageHeader.CreateHeader(messageHeaderAttribute.Name ?? messageHeaderMember.Name, messageHeaderAttribute.Namespace ?? operation.Contract.Namespace, messageHeaderMember.GetPropertyOrFieldValue(responseObject), messageHeaderAttribute.MustUnderstand));
				}
			}

			return responseMessage;
		}

		private void ExecuteFiltersAndTune(HttpContext httpContext, IServiceProvider serviceProvider, OperationDescription operation, object[] arguments, object serviceInstance)
		{
			// Execute model binding filters
			object modelBindingOutput = null;
			foreach (var modelBindingFilter in serviceProvider.GetServices<IModelBindingFilter>())
			{
				foreach (var modelType in modelBindingFilter.ModelTypes)
				{
					foreach (var parameterInfo in operation.InParameters)
					{
						var arg = arguments[parameterInfo.Index];
						if (arg != null && arg.GetType() == modelType)
						{
							modelBindingFilter.OnModelBound(arg, serviceProvider, out modelBindingOutput);
						}
					}
				}
			}

			// Execute Mvc ActionFilters
			foreach (var actionFilterAttr in operation.DispatchMethod.CustomAttributes.Where(a => a.AttributeType.Name == "ServiceFilterAttribute"))
			{
				var actionFilter = serviceProvider.GetService(actionFilterAttr.ConstructorArguments[0].Value as Type);
				actionFilter.GetType().GetMethod("OnSoapActionExecuting")?.Invoke(actionFilter, new[] { operation.Name, arguments, httpContext, modelBindingOutput });
			}

			// Invoke OnModelBound
			_options.SoapModelBounder?.OnModelBound(operation.DispatchMethod, arguments);

			// Tune service instance for operation call
			var serviceOperationTuners = serviceProvider.GetServices<IServiceOperationTuner>();
			foreach (var operationTuner in serviceOperationTuners)
			{
				operationTuner.Tune(httpContext, serviceInstance, operation);
			}
		}

		private void SetMessageHeadersToProperty(Message requestMessage, object serviceInstance)
		{
			var headerProperty = _service.ServiceType.GetProperty("MessageHeaders");
			if (headerProperty != null && headerProperty.PropertyType == requestMessage.Headers.GetType())
			{
				headerProperty.SetValue(serviceInstance, requestMessage.Headers);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private object[] GetRequestArguments(Message requestMessage, XmlDictionaryReader xmlReader, OperationDescription operation, HttpContext httpContext)
		{
			var arguments = new object[operation.AllParameters.Length];

			IEnumerable<Type> serviceKnownTypes = operation
				.GetServiceKnownTypesHierarchy()
				.Select(x => x.Type);

			if (!operation.IsMessageContractRequest)
			{
				if (xmlReader != null)
				{
					xmlReader.ReadStartElement(operation.Name, operation.Contract.Namespace);

					var lastParameterIndex = -1;
					while (!xmlReader.EOF)
					{
						var parameterInfo = operation.InParameters.FirstOrDefault(p => p.Name == xmlReader.LocalName);
						if (parameterInfo == null)
						{
							xmlReader.Skip();
							continue;
						}

						// prevent infinite loop (see https://github.com/DigDes/SoapCore/issues/610)
						if (parameterInfo.Index == lastParameterIndex)
						{
							break;
						}

						lastParameterIndex = parameterInfo.Index;

						var argumentValue = _serializerHelper.DeserializeInputParameter(
							xmlReader,
							parameterInfo.Parameter.ParameterType,
							parameterInfo.Name,
							operation.Contract.Namespace,
							parameterInfo.Parameter,
							serviceKnownTypes);

						//fix https://github.com/DigDes/SoapCore/issues/379 (hack, need research)
						if (argumentValue == null)
						{
							argumentValue = _serializerHelper.DeserializeInputParameter(
								xmlReader,
								parameterInfo.Parameter.ParameterType,
								parameterInfo.Name,
								parameterInfo.Namespace,
								parameterInfo.Parameter,
								serviceKnownTypes);
						}

						arguments[parameterInfo.Index] = argumentValue;
					}

					var httpContextParameter = operation.InParameters.FirstOrDefault(x => x.Parameter.ParameterType == typeof(HttpContext));
					if (httpContextParameter != default)
					{
						arguments[httpContextParameter.Index] = httpContext;
					}
				}
				else
				{
					arguments = Array.Empty<object>();
				}
			}
			else
			{
				// MessageContracts are constrained to having one "InParameter". We can do special logic on
				// for this
				Debug.Assert(operation.InParameters.Length == 1, "MessageContracts are constrained to having one 'InParameter'");

				var parameterInfo = operation.InParameters[0];
				var parameterType = parameterInfo.Parameter.ParameterType;

				var messageContractAttribute = parameterType.GetCustomAttribute<MessageContractAttribute>();

				Debug.Assert(messageContractAttribute != null, "operation.IsMessageContractRequest should be false if this is null");

				var @namespace = parameterInfo.Namespace ?? operation.Contract.Namespace;

				if (messageContractAttribute.IsWrapped && !parameterType.GetMembersWithAttribute<MessageHeaderAttribute>().Any())
				{
					//https://github.com/DigDes/SoapCore/issues/385
					if (operation.DispatchMethod.GetCustomAttribute<XmlSerializerFormatAttribute>()?.Style == OperationFormatStyle.Rpc)
					{
						DeserializeParameters(requestMessage, xmlReader, parameterType, parameterInfo, @namespace, serviceKnownTypes, messageContractAttribute, arguments);
					}
					else
					{
						// It's wrapped so either the wrapper name or the name of the wrapper type
						arguments[parameterInfo.Index] = _serializerHelper.DeserializeInputParameter(
							xmlReader,
							parameterInfo.Parameter.ParameterType,
							messageContractAttribute.WrapperName ?? parameterInfo.Parameter.ParameterType.Name,
							messageContractAttribute.WrapperNamespace ?? @namespace,
							parameterInfo.Parameter,
							serviceKnownTypes);
					}
				}
				else
				{
					DeserializeParameters(requestMessage, xmlReader, parameterType, parameterInfo, @namespace, serviceKnownTypes, messageContractAttribute, arguments);
				}
			}

			foreach (var parameterInfo in operation.OutParameters)
			{
				if (arguments[parameterInfo.Index] != null)
				{
					// do not overwrite input ref parameters
					continue;
				}

				if (parameterInfo.Parameter.ParameterType.Name == "Guid&")
				{
					arguments[parameterInfo.Index] = Guid.Empty;
				}
				else if (parameterInfo.Parameter.ParameterType.Name == "String&" || parameterInfo.Parameter.ParameterType.GetElementType().IsArray)
				{
					arguments[parameterInfo.Index] = null;
				}
				else
				{
					var type = parameterInfo.Parameter.ParameterType.GetElementType();
					arguments[parameterInfo.Index] = Activator.CreateInstance(type);
				}
			}

			return arguments;
		}

		private void DeserializeParameters(Message requestMessage, XmlDictionaryReader xmlReader, Type parameterType,
			SoapMethodParameterInfo parameterInfo, string @namespace, IEnumerable<Type> serviceKnownTypes,
			MessageContractAttribute messageContractAttribute, object[] arguments)
		{
			var messageHeadersMembers = parameterType.GetPropertyOrFieldMembers()
				.Where(x => x.GetCustomAttribute<MessageHeaderAttribute>() != null)
				.Select(mi => new
				{
					MemberInfo = mi,
					MessageHeaderMemberAttribute = mi.GetCustomAttribute<MessageHeaderAttribute>()
				}).ToArray();

			var wrapperObject = Activator.CreateInstance(parameterInfo.Parameter.ParameterType);

			for (var i = 0; i < requestMessage.Headers.Count; i++)
			{
				var header = requestMessage.Headers[i];
				var member = messageHeadersMembers.FirstOrDefault(x => x.MessageHeaderMemberAttribute.Name == header.Name || x.MemberInfo.Name == header.Name);

				if (member != null)
				{
					var reader = requestMessage.Headers.GetReaderAtHeader(i);

					var value = _serializerHelper.DeserializeInputParameter(
						reader,
						member.MemberInfo.GetPropertyOrFieldType(),
						member.MessageHeaderMemberAttribute.Name ?? member.MemberInfo.Name,
						member.MessageHeaderMemberAttribute.Namespace ?? @namespace,
						member.MemberInfo,
						serviceKnownTypes);

					member.MemberInfo.SetValueToPropertyOrField(wrapperObject, value);
				}
			}

			var messageBodyMembers = parameterType.GetPropertyOrFieldMembers().Where(x => x.GetCustomAttribute<MessageBodyMemberAttribute>() != null).Select(mi => new
				{
					Member = mi,
					MessageBodyMemberAttribute = mi.GetCustomAttribute<MessageBodyMemberAttribute>()
				}).OrderBy(x => x.MessageBodyMemberAttribute.Order);

			if (messageContractAttribute.IsWrapped)
			{
				xmlReader.Read();
			}

			foreach (var messageBodyMember in messageBodyMembers)
			{
				var messageBodyMemberAttribute = messageBodyMember.MessageBodyMemberAttribute;
				var messageBodyMemberInfo = messageBodyMember.Member;

				var innerParameterName = messageBodyMemberAttribute.Name ?? messageBodyMemberInfo.Name;
				var innerParameterNs = messageBodyMemberAttribute.Namespace ?? @namespace;
				var innerParameterType = messageBodyMemberInfo.GetPropertyOrFieldType();

				var innerParameter = _serializerHelper.DeserializeInputParameter(
					xmlReader,
					innerParameterType,
					innerParameterName,
					innerParameterNs,
					messageBodyMemberInfo,
					serviceKnownTypes);

				messageBodyMemberInfo.SetValueToPropertyOrField(wrapperObject, innerParameter);
			}

			arguments[parameterInfo.Index] = wrapperObject;
		}

		/// <summary>
		/// Helper message to write an error response message in case of an exception.
		/// </summary>
		/// <param name="exception">
		/// The exception that caused the failure.
		/// </param>
		/// <param name="statusCode">
		/// The HTTP status code that shall be returned to the caller.
		/// </param>
		/// <param name="serviceProvider">
		/// The DI container.
		/// </param>
		/// <param name="requestMessage">
		/// The Message for the incoming request
		/// </param>
		/// <param name="messageEncoder">
		/// Message encoder of incoming request
		/// </param>
		/// <param name="httpContext">
		/// The HTTP context that received the response message.
		/// </param>
		/// <returns>
		/// Returns the constructed message (which is implicitly written to the response
		/// and therefore must not be handled by the caller).
		/// </returns>
		private async Task<Message> WriteErrorResponseMessage(
			Exception exception,
			int statusCode,
			IServiceProvider serviceProvider,
			Message requestMessage,
			SoapMessageEncoder messageEncoder,
			HttpContext httpContext)
		{
			_logger.LogError(exception, "An error occurred processing the message");

			var xmlNamespaceManager = GetXmlNamespaceManager();
			var faultExceptionTransformer = serviceProvider.GetRequiredService<IFaultExceptionTransformer>();
			var faultMessage = faultExceptionTransformer.ProvideFault(exception, messageEncoder.MessageVersion, requestMessage, xmlNamespaceManager);

			httpContext.Response.ContentType = httpContext.Request.ContentType;
			httpContext.Response.Headers["SOAPAction"] = faultMessage.Headers.Action;
			httpContext.Response.StatusCode = statusCode;

			SetHttpResponse(httpContext, faultMessage);
			if (messageEncoder.MessageVersion.Addressing == AddressingVersion.WSAddressing10)
			{
				// TODO: Some additional work needs to be done in order to support setting the action. Simply setting it to
				// "http://www.w3.org/2005/08/addressing/fault" will cause the WCF Client to not be able to figure out the type
				faultMessage.Headers.RelatesTo = requestMessage.Headers.MessageId;
				faultMessage.Headers.To = requestMessage.Headers.ReplyTo?.Uri;
			}

			await WriteMessageAsync(messageEncoder, faultMessage, httpContext);

			return faultMessage;
		}

		private void SetHttpResponse(HttpContext httpContext, Message message)
		{
			if (!message.Properties.TryGetValue(HttpResponseMessageProperty.Name, out var value)
				|| !(value is HttpResponseMessageProperty httpProperty))
			{
				return;
			}

			httpContext.Response.StatusCode = (int)httpProperty.StatusCode;

			var feature = httpContext.Features.Get<IHttpResponseFeature>();
			if (feature != null && !string.IsNullOrEmpty(httpProperty.StatusDescription))
			{
				feature.ReasonPhrase = httpProperty.StatusDescription;
			}

			foreach (string key in httpProperty.Headers.Keys)
			{
				httpContext.Response.Headers.Add(key, httpProperty.Headers.GetValues(key));
			}
		}

		private async Task ProcessXSD(HttpContext httpContext)
		{
			var meta = new MetaFromFile();
			if (!string.IsNullOrEmpty(_options.WsdlFileOptions.VirtualPath))
			{
				meta.CurrentWebServer = _options.WsdlFileOptions.VirtualPath + "/";
			}

			meta.CurrentWebService = httpContext.Request.Path.Value.Replace("/", string.Empty);
			var mapping = _options.WsdlFileOptions.WebServiceWSDLMapping[meta.CurrentWebService];

			meta.XsdFolder = mapping.SchemaFolder;

			if (_options.WsdlFileOptions.UrlOverride != string.Empty)
			{
				meta.ServerUrl = _options.WsdlFileOptions.UrlOverride;
			}
			else
			{
				meta.ServerUrl = httpContext.Request.Scheme + "://" + httpContext.Request.Host + "/";
			}

			string xsdfile = httpContext.Request.Query["name"];

			//Check to prevent path traversal
			if (string.IsNullOrEmpty(xsdfile) || Path.GetFileName(xsdfile) != xsdfile)
			{
				throw new ArgumentNullException("xsd parameter contains illeagal values");
			}

			if (!xsdfile.Contains(".xsd"))
			{
				throw new Exception("xsd request must contain .xsd");
			}

			string path = _options.WsdlFileOptions.AppPath;
			string safePath = path + Path.AltDirectorySeparatorChar + meta.XsdFolder + Path.AltDirectorySeparatorChar + xsdfile;
			string xsd = await meta.ReadLocalFileAsync(safePath);
			string modifiedxsd = meta.ModifyXSDAddRightSchemaPath(xsd);

			//we should use text/xml in wsdl page for browser compability.
			httpContext.Response.ContentType = "text/xml;charset=UTF-8";
			await httpContext.Response.WriteAsync(modifiedxsd);
		}

		private async Task ProcessMetaFromFile(HttpContext httpContext)
		{
			var meta = new MetaFromFile();
			if (!string.IsNullOrEmpty(_options.WsdlFileOptions.VirtualPath))
			{
				meta.CurrentWebServer = _options.WsdlFileOptions.VirtualPath + "/";
			}

			meta.CurrentWebService = httpContext.Request.Path.Value.Replace("/", string.Empty);

			WebServiceWSDLMapping mapping = _options.WsdlFileOptions.WebServiceWSDLMapping[meta.CurrentWebService];

			meta.XsdFolder = mapping.SchemaFolder;
			meta.WSDLFolder = mapping.WSDLFolder;
			if (_options.WsdlFileOptions.UrlOverride != string.Empty)
			{
				meta.ServerUrl = _options.WsdlFileOptions.UrlOverride;
			}
			else
			{
				meta.ServerUrl = httpContext.Request.Scheme + "://" + httpContext.Request.Host + "/";
			}

			string wsdlfile = mapping.WsdlFile;

			string path = _options.WsdlFileOptions.AppPath;
			string wsdl = await meta.ReadLocalFileAsync(path + Path.AltDirectorySeparatorChar + meta.WSDLFolder + Path.AltDirectorySeparatorChar + wsdlfile);
			string modifiedWsdl = meta.ModifyWSDLAddRightSchemaPath(wsdl);

			//we should use text/xml in wsdl page for browser compability.
			httpContext.Response.ContentType = "text/xml;charset=UTF-8";
			await httpContext.Response.WriteAsync(modifiedWsdl);
		}

		private XmlNamespaceManager GetXmlNamespaceManager()
		{
			var xmlNamespaceManager = _options.XmlNamespacePrefixOverrides ?? new XmlNamespaceManager(new NameTable());
			Namespaces.AddDefaultNamespaces(xmlNamespaceManager);
			return xmlNamespaceManager;
		}
	}
}
