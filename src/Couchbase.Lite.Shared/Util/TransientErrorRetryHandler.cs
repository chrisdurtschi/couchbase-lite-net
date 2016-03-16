﻿using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;

namespace Couchbase.Lite.Util
{
    internal sealed class TransientErrorRetryHandler : DelegatingHandler
    {
        private static readonly string Tag = typeof(TransientErrorRetryHandler).Name;
        private readonly IRetryStrategy _retryStrategy;

        public TransientErrorRetryHandler(HttpMessageHandler handler, IRetryStrategy strategy) : base(handler) 
        { 
            InnerHandler = handler;
            _retryStrategy = strategy.Copy();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var executor = new RetryStrategyExecutor(request, _retryStrategy, cancellationToken);
            executor.Send = ResendHandler;
            return ResendHandler(request, executor);
        }

        private Task<HttpResponseMessage> ResendHandler(HttpRequestMessage request, RetryStrategyExecutor executor)
        {
            return base.SendAsync(request, executor.Token)
                .ContinueWith(t => HandleTransientErrors(t, executor), executor.Token)
                .Unwrap();
        }

       
        static Task<HttpResponseMessage> HandleTransientErrors(Task<HttpResponseMessage> request, object state)
        {
            var executor = (RetryStrategyExecutor)state;
            if (!request.IsFaulted) 
            {
                var response = request.Result;
                if (executor.CanContinue && Misc.IsTransientError(response)) {
                    return executor.Retry();
                }

                if (!response.IsSuccessStatusCode) {
                    Log.To.Sync.V(Tag, "Non transient error received ({0}), throwing HttpResponseException", 
                        response.StatusCode);
                    throw new HttpResponseException(response.StatusCode);
                }

                // If it's not faulted, there's nothing here to do.
                return request;
            }

            var error = Misc.Flatten(request.Exception);

            string statusCode;
            if (!Misc.IsTransientNetworkError(error, out statusCode) || !executor.CanContinue)
            {
                if (!executor.CanContinue) {
                    Log.To.Sync.V(Tag, "Out of retries for error, throwing", error);
                } else {
                    Log.To.Sync.V(Tag, "Non transient error received (status), throwing", error);
                }

                // If it's not transient, pass the exception along
                // for any other handlers to respond to.
                throw error;
            }

            // Retry again.
            return executor.Retry();
        }
    }
}

