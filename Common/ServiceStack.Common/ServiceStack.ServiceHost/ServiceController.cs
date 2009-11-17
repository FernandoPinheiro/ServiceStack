using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using ServiceStack.Common.Utils;
using ServiceStack.Configuration;
using ServiceStack.Logging;

namespace ServiceStack.ServiceHost
{
	public class ServiceController
		: IServiceController
	{
		private static readonly ILog Log = LogManager.GetLogger(typeof(ServiceController));
		private const string ResponseDtoSuffix = "Response";

		public ServiceController()
		{
			this.AllOperationTypes = new List<Type>();
			this.OperationTypes = new List<Type>();
			this.ServiceTypes = new HashSet<Type>();
		}

		readonly Dictionary<Type, Func<IRequestContext, object, object>> requestExecMap 
			= new Dictionary<Type, Func<IRequestContext, object, object>>();

		public IList<Type> AllOperationTypes { get; protected set; }

		public IList<Type> OperationTypes { get; protected set; }

		public HashSet<Type> ServiceTypes { get; protected set; }

		public string DefaultOperationsNamespace { get; set; }

		public void Register<TServiceRequest>(Func<IService<TServiceRequest>> invoker)
		{
			var requestType = typeof(TServiceRequest);

			Func<IRequestContext, object, object> handlerFn = (requestContext, dto) => {
				var service = invoker();
				InjectRequestContext(service, requestContext);
				return service.Execute((TServiceRequest)dto);
			};

			requestExecMap.Add(requestType, handlerFn);
		}

		public void Register(ITypeFactory serviceFactoryFn, params Assembly[] assembliesWithServices)
		{
			foreach (var assembly in assembliesWithServices)
			{
				foreach (var serviceType in assembly.GetTypes())
				{
					foreach (var service in serviceType.GetInterfaces())
					{
						if (serviceType.IsAbstract
							|| !service.IsGenericType
							|| service.GetGenericTypeDefinition() != typeof(IService<>)
							) continue;

						var requestType = service.GetGenericArguments()[0];

						Register(requestType, serviceType, serviceFactoryFn);

						this.ServiceTypes.Add(serviceType);

						this.AllOperationTypes.Add(requestType);
						this.OperationTypes.Add(requestType);

						var responseTypeName = requestType.FullName + ResponseDtoSuffix;
						var responseType = AssemblyUtils.FindType(responseTypeName);
						if (responseType != null)
						{
							this.AllOperationTypes.Add(responseType);
							this.OperationTypes.Add(responseType);
						}

						Log.DebugFormat("Registering {0} service '{1}' with request '{2}'",
							(responseType != null ? "SyncReply" : "OneWay"),
							serviceType.Name, requestType.Name);
					}
				}
			}
		}

		internal class TypeFactoryWrapper : ITypeFactory
		{
			private readonly Func<Type, object> typeCreator;

			public TypeFactoryWrapper(Func<Type, object> typeCreator)
			{
				this.typeCreator = typeCreator;
			}

			public object CreateInstance(Type type)
			{
				return typeCreator(type);
			}
		}

		public void Register(Type requestType, Type serviceType)
		{
			var handlerFactoryFn = Expression.Lambda<Func<Type, object>>
				(
					Expression.New(serviceType),
					Expression.Parameter(typeof(Type), "serviceType")
				).Compile();

			Register(requestType, serviceType, new TypeFactoryWrapper(handlerFactoryFn));
		}

		public void Register(Type requestType, Type serviceType, Func<Type, object> handlerFactoryFn)
		{
			Register(requestType, serviceType, new TypeFactoryWrapper(handlerFactoryFn));
		}

		public void Register(Type requestType, Type serviceType, ITypeFactory serviceFactoryFn)
		{
			var typeFactoryFn = CallServiceExecuteGeneric(requestType, serviceType);

			Func<IRequestContext, object, object> handlerFn = (requestContext, dto) => {
				var service = serviceFactoryFn.CreateInstance(serviceType);
				InjectRequestContext(service, requestContext);
				return typeFactoryFn(dto, service);
			};
			requestExecMap.Add(requestType, handlerFn);
		}

		private static void InjectRequestContext(object service, IRequestContext requestContext)
		{
			if (requestContext == null) return;

			var serviceRequiresContext = service as IRequiresRequestContext;
			if (serviceRequiresContext != null)
			{
				serviceRequiresContext.RequestContext = requestContext;
			}
		}

		private static Func<object, object, object> CallServiceExecuteGeneric(Type requestType, Type serviceType)
		{
			var requestDtoParam = Expression.Parameter(typeof(object), "requestDto");
			var requestDtoStrong = Expression.Convert(requestDtoParam, requestType);

			var serviceParam = Expression.Parameter(typeof(object), "serviceObj");
			var serviceStrong = Expression.Convert(serviceParam, serviceType);

			var mi = serviceType.GetMethod("Execute", new[] { requestType });

			Expression callExecute = Expression.Call(serviceStrong, mi, new[] { requestDtoStrong });

			var executeFunc = Expression.Lambda<Func<object, object, object>>
				(callExecute, requestDtoParam, serviceParam).Compile();

			return executeFunc;
		}

		public object Execute(object dto)
		{
			return Execute(dto, null);
		}

		public object Execute(object request, IRequestContext requestContext)
		{
			var handlerFn = GetService(request.GetType());
			return handlerFn(requestContext, request);
		}

		public Func<IRequestContext, object, object> GetService(Type requestType)
		{
			Func<IRequestContext, object, object> handlerFn;
			if (!requestExecMap.TryGetValue(requestType, out handlerFn))
			{
				throw new NotImplementedException(
						string.Format("Unable to resolve service '{0}'", requestType.Name));
			}

			return handlerFn;
		}

		public object ExecuteText(string text, IRequestContext requestContext)
		{
			throw new NotImplementedException();
		}

	}

}