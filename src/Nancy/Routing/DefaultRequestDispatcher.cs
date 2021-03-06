namespace Nancy.Routing
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using Nancy.Helpers;

    using Responses.Negotiation;

    /// <summary>
    /// Default implementation of a request dispatcher.
    /// </summary>
    public class DefaultRequestDispatcher : IRequestDispatcher
    {
        private readonly IRouteResolver routeResolver;
        private readonly IEnumerable<IResponseProcessor> responseProcessors;
        private readonly IRouteInvoker routeInvoker;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultRequestDispatcher"/> class, with
        /// the provided <paramref name="routeResolver"/>, <paramref name="responseProcessors"/> and <paramref name="routeInvoker"/>.
        /// </summary>
        /// <param name="routeResolver"></param>
        /// <param name="responseProcessors"></param>
        /// <param name="routeInvoker"></param>
        public DefaultRequestDispatcher(IRouteResolver routeResolver, IEnumerable<IResponseProcessor> responseProcessors, IRouteInvoker routeInvoker)
        {
            this.routeResolver = routeResolver;
            this.responseProcessors = responseProcessors;
            this.routeInvoker = routeInvoker;
        }

        /// <summary>
        /// Dispatches a requests.
        /// </summary>
        /// <param name="context">The <see cref="NancyContext"/> for the current request.</param>
        public Task<Response> Dispatch(NancyContext context, CancellationToken cancellationToken)
        {
            // TODO - May need to make this run off context rather than response .. seems a bit icky currently
            var tcs = new TaskCompletionSource<Response>();

            var resolveResult = this.Resolve(context);

            var preReqTask = ExecuteRoutePreReq(context, cancellationToken, resolveResult.Before);

            preReqTask.WhenCompleted(
                completedTask =>
                    {
                        context.Response = completedTask.Result;

                        if (context.Response == null)
                        {
                            var routeTask = this.routeInvoker.Invoke(resolveResult.Route, cancellationToken, resolveResult.Parameters, context);

                            routeTask.WhenCompleted(
                                completedRouteTask =>
                                    {
                                        context.Response = completedRouteTask.Result;

                                        ExecutePost(context, cancellationToken, resolveResult.After, tcs);
                                    },
                                HandleFaultedTask(context, resolveResult.OnError, tcs));
                            
                            return;
                        }

                        ExecutePost(context, cancellationToken, resolveResult.After, tcs);
                    },
                HandleFaultedTask(context, resolveResult.OnError, tcs));

            return tcs.Task;
        }

        private void ExecutePost(NancyContext context, CancellationToken cancellationToken, AfterPipeline postHook, TaskCompletionSource<Response> tcs)
        {
            if (postHook == null)
            {
                tcs.SetResult(context.Response);
                return;
            }

            postHook.Invoke(context, cancellationToken).WhenCompleted(
                                        t => tcs.SetResult(context.Response),
                                        t => tcs.SetException(t.Exception),
                                        false);
        }

        private Action<Task<Response>> HandleFaultedTask(NancyContext context, Func<NancyContext, Exception, Response> onError, TaskCompletionSource<Response> tcs)
        {
            return task =>
                {
                    var response = ResolveErrorResult(context, onError, task.Exception);
    
                    if (response != null)
                    {
                        context.Response = response;

                        tcs.SetResult(response);
                    }
                    else
                    {
                        tcs.SetException(task.Exception);
                    }
                };
        }

        private static Task<Response> ExecuteRoutePreReq(NancyContext context, CancellationToken cancellationToken, BeforePipeline resolveResultPreReq)
        {
            if (resolveResultPreReq == null)
            {
                return TaskHelpers.GetCompletedTask<Response>(null);
            }

            return resolveResultPreReq.Invoke(context, cancellationToken);
        }

        private static Response ResolveErrorResult(NancyContext context, Func<NancyContext, Exception, Response> resolveResultOnError, Exception exception)
        {
            if (resolveResultOnError != null)
            {
                return resolveResultOnError.Invoke(context, exception);
            }

            return null;
        }

        private ResolveResult Resolve(NancyContext context)
        {
            var extension =
                Path.GetExtension(context.Request.Path);

            var originalAcceptHeaders = context.Request.Headers.Accept;
            var originalRequestPath = context.Request.Path;

            if (!string.IsNullOrEmpty(extension))
            {
                var mappedMediaRanges = this.GetMediaRangesForExtension(extension.Substring(1))
                    .ToArray();

                if (mappedMediaRanges.Any())
                {
                    var newMediaRanges =
                        mappedMediaRanges.Where(x => !context.Request.Headers.Accept.Any(header => header.Equals(x)));

                    var modifiedRequestPath = 
                        context.Request.Path.Replace(extension, string.Empty);

                    var match =
                        this.InvokeRouteResolver(context, modifiedRequestPath, newMediaRanges);

                    if (!(match.Route is NotFoundRoute))
                    {
                        return match;
                    }
                }
            }

            return this.InvokeRouteResolver(context, originalRequestPath, originalAcceptHeaders);
        }

        private IEnumerable<Tuple<string, decimal>> GetMediaRangesForExtension(string extension)
        {
            return this.responseProcessors
                .SelectMany(processor => processor.ExtensionMappings)
                .Where(mapping => mapping != null)
                .Where(mapping => mapping.Item1.Equals(extension, StringComparison.OrdinalIgnoreCase))
                .Select(mapping => new Tuple<string, decimal>(mapping.Item2, Decimal.MaxValue))
                .Distinct();
        }

        private ResolveResult InvokeRouteResolver(NancyContext context, string path, IEnumerable<Tuple<string, decimal>> acceptHeaders)
        {
            context.Request.Headers.Accept = acceptHeaders.ToList();
            context.Request.Url.Path = path;

            return this.routeResolver.Resolve(context);
        }
    }
}
