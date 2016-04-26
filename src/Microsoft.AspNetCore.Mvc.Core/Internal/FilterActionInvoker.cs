// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Core;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public abstract class FilterActionInvoker : IActionInvoker
    {
        private readonly ControllerActionInvokerCache _controllerActionInvokerCache;
        private readonly IReadOnlyList<IInputFormatter> _inputFormatters;
        private readonly IReadOnlyList<IValueProviderFactory> _valueProviderFactories;
        private readonly DiagnosticSource _diagnosticSource;
        private readonly int _maxModelValidationErrors;

        private IFilterMetadata[] _filters;
        private ObjectMethodExecutor _controllerActionMethodExecutor;
        private FilterCursor _cursor;

        private AuthorizationFilterContext _authorizationContext;

        private ResourceExecutingContext _resourceExecutingContext;
        private ResourceExecutedContext _resourceExecutedContext;

        private ExceptionContext _exceptionContext;

        private ActionExecutingContext _actionExecutingContext;
        private ActionExecutedContext _actionExecutedContext;

        private ResultExecutingContext _resultExecutingContext;
        private ResultExecutedContext _resultExecutedContext;

        private IActionResult _result;

        public FilterActionInvoker(
            ActionContext actionContext,
            ControllerActionInvokerCache controllerActionInvokerCache,
            IReadOnlyList<IInputFormatter> inputFormatters,
            IReadOnlyList<IValueProviderFactory> valueProviderFactories,
            ILogger logger,
            DiagnosticSource diagnosticSource,
            int maxModelValidationErrors)
        {
            if (actionContext == null)
            {
                throw new ArgumentNullException(nameof(actionContext));
            }

            if (controllerActionInvokerCache == null)
            {
                throw new ArgumentNullException(nameof(controllerActionInvokerCache));
            }

            if (inputFormatters == null)
            {
                throw new ArgumentNullException(nameof(inputFormatters));
            }

            if (valueProviderFactories == null)
            {
                throw new ArgumentNullException(nameof(valueProviderFactories));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (diagnosticSource == null)
            {
                throw new ArgumentNullException(nameof(diagnosticSource));
            }


            _controllerActionInvokerCache = controllerActionInvokerCache;
            _inputFormatters = inputFormatters;
            _valueProviderFactories = valueProviderFactories;
            Logger = logger;
            _diagnosticSource = diagnosticSource;
            _maxModelValidationErrors = maxModelValidationErrors;

            Context = new ControllerContext(actionContext);
            Context.ModelState.MaxAllowedErrors = _maxModelValidationErrors;
            Context.ValueProviderFactories = new List<IValueProviderFactory>(_valueProviderFactories);
        }

        protected ControllerContext Context { get; }

        protected object Instance { get; private set; }

        protected ILogger Logger { get; }

        /// <summary>
        /// Called to create an instance of an object which will act as the reciever of the action invocation.
        /// </summary>
        /// <returns>The constructed instance or <c>null</c>.</returns>
        protected abstract object CreateInstance();

        /// <summary>
        /// Called to create an instance of an object which will act as the reciever of the action invocation.
        /// </summary>
        /// <param name="instance">The instance to release.</param>
        /// <remarks>This method will not be called if <see cref="CreateInstance"/> returns <c>null</c>.</remarks>
        protected abstract void ReleaseInstance(object instance);

        protected abstract Task<IActionResult> InvokeActionAsync(ActionExecutingContext actionExecutingContext);

        protected abstract Task BindActionArgumentsAsync(IDictionary<string, object> arguments);

        public virtual async Task InvokeAsync()
        {
            var controllerActionInvokerState = _controllerActionInvokerCache.GetState(Context);
            _filters = controllerActionInvokerState.Filters;
            _controllerActionMethodExecutor = controllerActionInvokerState.ActionMethodExecutor;
            _cursor = new FilterCursor(_filters);

            await InvokeAllAuthorizationFiltersAsync();

            // If Authorization Filters return a result, it's a short circuit because
            // authorization failed. We don't execute Result Filters around the result.
            if (_authorizationContext?.Result != null)
            {
                await InvokeResultAsync(_authorizationContext.Result);
                return;
            }

            try
            {
                await InvokeAllResourceFiltersAsync();
            }
            finally
            {
                // Release the instance after all filters have run. We don't need to surround
                // Authorizations filters because the instance will be created much later than
                // that.
                if (Instance != null)
                {
                    ReleaseInstance(Instance);
                }
            }

            // We've reached the end of resource filters. If there's an unhandled exception on the context then
            // it should be thrown and middleware has a chance to handle it.
            Debug.Assert(_resourceExecutedContext != null);
            if (_resourceExecutedContext.Exception != null && !_resourceExecutedContext.ExceptionHandled)
            {
                if (_resourceExecutedContext.ExceptionDispatchInfo == null)
                {
                    throw _resourceExecutedContext.Exception;
                }
                else
                {
                    _resourceExecutedContext.ExceptionDispatchInfo.Throw();
                }
            }
        }

        protected ObjectMethodExecutor GetControllerActionMethodExecutor()
        {
            return _controllerActionMethodExecutor;
        }

        private Task InvokeAllAuthorizationFiltersAsync()
        {
            _cursor.Reset();

            return InvokeNextAuthorizationFilterAsync();
        }

        private Task InvokeNextAuthorizationFilterAsync()
        {
            // We should never get here if we already have a result.
            Debug.Assert(_authorizationContext?.Result == null);

            var current = _cursor.GetNextFilter<IAuthorizationFilter, IAsyncAuthorizationFilter>();
            if (current.FilterAsync != null)
            {
                _authorizationContext = _authorizationContext ?? new AuthorizationFilterContext(Context, _filters);
                return InvokeAsyncAuthorizationFilterAsync(current.FilterAsync);
            }
            else if (current.Filter != null)
            {
                _authorizationContext = _authorizationContext ?? new AuthorizationFilterContext(Context, _filters);
                return InvokeSyncAuthorizationFilterAsync(current.Filter);
            }
            else
            {
                return TaskCache.CompletedTask;
            }
        }

        private async Task InvokeSyncAuthorizationFilterAsync(IAuthorizationFilter filter)
        {
            _diagnosticSource.BeforeOnAuthorization(_authorizationContext, filter);
            filter.OnAuthorization(_authorizationContext);
            _diagnosticSource.AfterOnAuthorization(_authorizationContext, filter);

            if (_authorizationContext.Result == null)
            {
                // Only keep going if we don't have a result
                await InvokeNextAuthorizationFilterAsync();
            }
            else
            {
                Logger.AuthorizationFailure(filter);
            }

        }

        private async Task InvokeAsyncAuthorizationFilterAsync(IAsyncAuthorizationFilter filter)
        {
            _diagnosticSource.BeforeOnAuthorizationAsync(_authorizationContext, filter);
            await filter.OnAuthorizationAsync(_authorizationContext);
            _diagnosticSource.AfterOnAuthorizationAsync(_authorizationContext, filter);

            if (_authorizationContext.Result == null)
            {
                // Only keep going if we don't have a result
                await InvokeNextAuthorizationFilterAsync();
            }
            else
            {
                Logger.AuthorizationFailure(filter);
            }
        }

        private Task InvokeAllResourceFiltersAsync()
        {
            _cursor.Reset();

            _resourceExecutingContext = new ResourceExecutingContext(Context, _filters);
            return InvokeResourceFilterAsync();
        }

        private async Task<ResourceExecutedContext> InvokeResourceFilterAwaitedAsync()
        {
            await InvokeResourceFilterAsync();
            return _resourceExecutedContext;
        }

        private async Task InvokeResourceFilterAsync()
        {
            Debug.Assert(_resourceExecutingContext != null);

            if (_resourceExecutingContext.Result != null)
            {
                // If we get here, it means that an async filter set a result AND called next(). This is forbidden.
                var message = Resources.FormatAsyncResourceFilter_InvalidShortCircuit(
                    typeof(IAsyncResourceFilter).Name,
                    nameof(ResourceExecutingContext.Result),
                    typeof(ResourceExecutingContext).Name,
                    typeof(ResourceExecutionDelegate).Name);

                throw new InvalidOperationException(message);
            }

            var item = _cursor.GetNextFilter<IResourceFilter, IAsyncResourceFilter>();
            try
            {
                if (item.FilterAsync != null)
                {
                    _diagnosticSource.BeforeOnResourceExecution(
                        _resourceExecutingContext,
                        item.FilterAsync);

                    await item.FilterAsync.OnResourceExecutionAsync(
                        _resourceExecutingContext,
                        InvokeResourceFilterAwaitedAsync);

                    _diagnosticSource.AfterOnResourceExecution(
                        _resourceExecutingContext.ActionDescriptor,
                        _resourceExecutedContext,
                        item.FilterAsync);

                    if (_resourceExecutedContext == null)
                    {
                        // If we get here then the filter didn't call 'next' indicating a short circuit
                        if (_resourceExecutingContext.Result != null)
                        {
                            Logger.ResourceFilterShortCircuited(item.FilterAsync);

                            await InvokeResultAsync(_resourceExecutingContext.Result);
                        }

                        _resourceExecutedContext = new ResourceExecutedContext(_resourceExecutingContext, _filters)
                        {
                            Canceled = true,
                            Result = _resourceExecutingContext.Result,
                        };
                    }
                }
                else if (item.Filter != null)
                {
                    _diagnosticSource.BeforeOnResourceExecuting(
                        _resourceExecutingContext,
                        item.Filter);

                    item.Filter.OnResourceExecuting(_resourceExecutingContext);

                    _diagnosticSource.AfterOnResourceExecuting(
                        _resourceExecutingContext,
                        item.Filter);

                    if (_resourceExecutingContext.Result != null)
                    {
                        // Short-circuited by setting a result.
                        Logger.ResourceFilterShortCircuited(item.Filter);

                        await InvokeResultAsync(_resourceExecutingContext.Result);

                        _resourceExecutedContext = new ResourceExecutedContext(_resourceExecutingContext, _filters)
                        {
                            Canceled = true,
                            Result = _resourceExecutingContext.Result,
                        };
                    }
                    else
                    {
                        _diagnosticSource.BeforeOnResourceExecuted(
                            _resourceExecutingContext.ActionDescriptor,
                            _resourceExecutedContext,
                            item.Filter);

                        await InvokeResourceFilterAsync();
                        item.Filter.OnResourceExecuted(_resourceExecutedContext);

                        _diagnosticSource.AfterOnResourceExecuted(
                            _resourceExecutingContext.ActionDescriptor,
                            _resourceExecutedContext,
                            item.Filter);
                    }
                }
                else
                {
                    // >> ExceptionFilters >> Model Binding >> ActionFilters >> Action
                    await InvokeAllExceptionFiltersAsync();

                    // If Exception Filters provide a result, it's a short-circuit due to an exception.
                    // We don't execute Result Filters around the result.
                    Debug.Assert(_exceptionContext != null);
                    if (_exceptionContext.Result != null)
                    {
                        // This means that exception filters returned a result to 'handle' an error.
                        // We're not interested in seeing the exception details since it was handled.
                        await InvokeResultAsync(_exceptionContext.Result);

                        _resourceExecutedContext = new ResourceExecutedContext(_resourceExecutingContext, _filters)
                        {
                            Result = _exceptionContext.Result,
                        };
                    }
                    else if (_exceptionContext.Exception != null)
                    {
                        // If we get here, this means that we have an unhandled exception.
                        // Exception filters didn't handle this, so send it on to resource filters.
                        _resourceExecutedContext = new ResourceExecutedContext(_resourceExecutingContext, _filters);

                        // Preserve the stack trace if possible.
                        _resourceExecutedContext.Exception = _exceptionContext.Exception;
                        if (_exceptionContext.ExceptionDispatchInfo != null)
                        {
                            _resourceExecutedContext.ExceptionDispatchInfo = _exceptionContext.ExceptionDispatchInfo;
                        }
                    }
                    else
                    {
                        // We have a successful 'result' from the action or an Action Filter, so run
                        // Result Filters.
                        Debug.Assert(_actionExecutedContext != null);
                        _result = _actionExecutedContext.Result;

                        // >> ResultFilters >> (Result)
                        _cursor.Reset();
                        await InvokeNextResultFilterAsync();

                        if (_resultExecutedContext?.Exception != null &&
                            _resultExecutedContext?.ExceptionHandled != true)
                        {
                            // If we get here, this means that we have an unhandled exception.
                            // Result filters didn't handle this, so send it on to resource filters.
                            _resourceExecutedContext = new ResourceExecutedContext(_resourceExecutingContext, _filters);

                            // Preserve the stack trace if possible.
                            _resourceExecutedContext.Exception = _resultExecutedContext.Exception;
                            if (_resultExecutedContext.ExceptionDispatchInfo != null)
                            {
                                _resourceExecutedContext.ExceptionDispatchInfo = _resultExecutedContext.ExceptionDispatchInfo;
                            }
                        }
                        else
                        {
                            _resourceExecutedContext = new ResourceExecutedContext(_resourceExecutingContext, _filters)
                            {
                                Result = _resultExecutedContext?.Result ?? _result,
                            };
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                _resourceExecutedContext = new ResourceExecutedContext(_resourceExecutingContext, _filters)
                {
                    ExceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception)
                };
            }

            Debug.Assert(_resourceExecutedContext != null);
        }

        private Task InvokeAllExceptionFiltersAsync()
        {
            _cursor.Reset();

            return InvokeExceptionFilterAsync();
        }

        private async Task InvokeExceptionFilterAsync()
        {
            var current = _cursor.GetNextFilter<IExceptionFilter, IAsyncExceptionFilter>();
            if (current.FilterAsync != null)
            {
                // Exception filters run "on the way out" - so the filter is run after the rest of the
                // pipeline.
                await InvokeExceptionFilterAsync();

                Debug.Assert(_exceptionContext != null);
                if (_exceptionContext.Exception != null)
                {
                    _diagnosticSource.BeforeOnExceptionAsync(
                        _exceptionContext,
                        current.FilterAsync);

                    // Exception filters only run when there's an exception - unsetting it will short-circuit
                    // other exception filters.
                    await current.FilterAsync.OnExceptionAsync(_exceptionContext);

                    _diagnosticSource.AfterOnExceptionAsync(
                        _exceptionContext,
                        current.FilterAsync);

                    if (_exceptionContext.Exception == null)
                    {
                        Logger.ExceptionFilterShortCircuited(current.FilterAsync);
                    }
                }
            }
            else if (current.Filter != null)
            {
                // Exception filters run "on the way out" - so the filter is run after the rest of the
                // pipeline.
                await InvokeExceptionFilterAsync();

                Debug.Assert(_exceptionContext != null);
                if (_exceptionContext.Exception != null)
                {
                    _diagnosticSource.BeforeOnException(
                        _exceptionContext,
                        current.Filter);

                    // Exception filters only run when there's an exception - unsetting it will short-circuit
                    // other exception filters.
                    current.Filter.OnException(_exceptionContext);

                    _diagnosticSource.AfterOnException(
                        _exceptionContext,
                        current.Filter);

                    if (_exceptionContext.Exception == null)
                    {
                        Logger.ExceptionFilterShortCircuited(current.Filter);
                    }
                }
            }
            else
            {
                // We've reached the 'end' of the exception filter pipeline - this means that one stack frame has
                // been built for each exception. When we return from here, these frames will either:
                //
                // 1) Call the filter (if we have an exception)
                // 2) No-op (if we don't have an exception)
                Debug.Assert(_exceptionContext == null);
                _exceptionContext = new ExceptionContext(Context, _filters);

                try
                {
                    await InvokeAllActionFiltersAsync();

                    // Action filters might 'return' an unhandled exception instead of throwing
                    Debug.Assert(_actionExecutedContext != null);
                    if (_actionExecutedContext.Exception != null && !_actionExecutedContext.ExceptionHandled)
                    {
                        _exceptionContext.Exception = _actionExecutedContext.Exception;
                        if (_actionExecutedContext.ExceptionDispatchInfo != null)
                        {
                            _exceptionContext.ExceptionDispatchInfo = _actionExecutedContext.ExceptionDispatchInfo;
                        }
                    }
                }
                catch (Exception exception)
                {
                    _exceptionContext.ExceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception);
                }
            }
        }

        private async Task InvokeAllActionFiltersAsync()
        {
            _cursor.Reset();

            Instance = CreateInstance();

            var arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            await BindActionArgumentsAsync(arguments);
            _actionExecutingContext = new ActionExecutingContext(Context, _filters, arguments, Instance);

            await InvokeActionFilterAsync();
        }

        private async Task<ActionExecutedContext> InvokeActionFilterAwaitedAsync()
        {
            await InvokeActionFilterAsync();
            return _actionExecutedContext;
        }

        private async Task InvokeActionFilterAsync()
        {
            Debug.Assert(_actionExecutingContext != null);
            if (_actionExecutingContext.Result != null)
            {
                // If we get here, it means that an async filter set a result AND called next(). This is forbidden.
                var message = Resources.FormatAsyncActionFilter_InvalidShortCircuit(
                    typeof(IAsyncActionFilter).Name,
                    nameof(ActionExecutingContext.Result),
                    typeof(ActionExecutingContext).Name,
                    typeof(ActionExecutionDelegate).Name);

                throw new InvalidOperationException(message);
            }

            var item = _cursor.GetNextFilter<IActionFilter, IAsyncActionFilter>();
            try
            {
                if (item.FilterAsync != null)
                {
                    _diagnosticSource.BeforeOnActionExecution(
                        _actionExecutingContext,
                        item.FilterAsync);

                    await item.FilterAsync.OnActionExecutionAsync(_actionExecutingContext, InvokeActionFilterAwaitedAsync);

                    _diagnosticSource.AfterOnActionExecution(
                        _actionExecutingContext.ActionDescriptor,
                        _actionExecutedContext,
                        item.FilterAsync);

                    if (_actionExecutedContext == null)
                    {
                        // If we get here then the filter didn't call 'next' indicating a short circuit
                        Logger.ActionFilterShortCircuited(item.FilterAsync);

                        _actionExecutedContext = new ActionExecutedContext(
                            _actionExecutingContext,
                            _filters,
                            Instance)
                        {
                            Canceled = true,
                            Result = _actionExecutingContext.Result,
                        };
                    }
                }
                else if (item.Filter != null)
                {
                    _diagnosticSource.BeforeOnActionExecuting(
                        _actionExecutingContext,
                        item.Filter);

                    item.Filter.OnActionExecuting(_actionExecutingContext);

                    _diagnosticSource.AfterOnActionExecuting(
                        _actionExecutingContext,
                        item.Filter);

                    if (_actionExecutingContext.Result != null)
                    {
                        // Short-circuited by setting a result.
                        Logger.ActionFilterShortCircuited(item.Filter);

                        _actionExecutedContext = new ActionExecutedContext(
                            _actionExecutingContext,
                            _filters,
                            Instance)
                        {
                            Canceled = true,
                            Result = _actionExecutingContext.Result,
                        };
                    }
                    else
                    {
                        _diagnosticSource.BeforeOnActionExecuted(
                            _actionExecutingContext.ActionDescriptor,
                            _actionExecutedContext,
                            item.Filter);

                        await InvokeActionFilterAsync();
                        item.Filter.OnActionExecuted(_actionExecutedContext);

                        _diagnosticSource.BeforeOnActionExecuted(
                            _actionExecutingContext.ActionDescriptor,
                            _actionExecutedContext,
                            item.Filter);
                    }
                }
                else
                {
                    // All action filters have run, execute the action method.
                    IActionResult result = null;

                    try
                    {
                        _diagnosticSource.BeforeActionMethod(
                            Context,
                            _actionExecutingContext.ActionArguments,
                            _actionExecutingContext.Controller);

                        result = await InvokeActionAsync(_actionExecutingContext);
                    }
                    finally
                    {
                        _diagnosticSource.AfterActionMethod(
                            Context,
                            _actionExecutingContext.ActionArguments,
                            _actionExecutingContext.Controller,
                            result);
                    }

                    _actionExecutedContext = new ActionExecutedContext(
                        _actionExecutingContext,
                        _filters,
                        Instance)
                    {
                        Result = result
                    };
                }
            }
            catch (Exception exception)
            {
                // Exceptions thrown by the action method OR filters bubble back up through ActionExcecutedContext.
                _actionExecutedContext = new ActionExecutedContext(
                    _actionExecutingContext,
                    _filters,
                    Instance)
                {
                    ExceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception)
                };
            }
        }

        private Task InvokeNextResultFilterAsync()
        {
            var current = _cursor.GetNextFilter<IResultFilter, IAsyncResultFilter>();
            if (current.FilterAsync != null)
            {
                _resultExecutingContext = _resultExecutingContext ?? new ResultExecutingContext(Context, _filters, _result, Instance);
                return InvokeAsyncResultFilterAsync(current.FilterAsync);
            }
            else if (current.Filter != null)
            {
                _resultExecutingContext = _resultExecutingContext ?? new ResultExecutingContext(Context, _filters, _result, Instance);
                return InvokeSyncResultFilterAsync(current.Filter);
            }
            else if (_resultExecutingContext != null)
            {
                // The empty result is always flowed back as the 'executed' result
                if (_resultExecutingContext.Result == null)
                {
                    _resultExecutingContext.Result = new EmptyResult();
                }

                return InvokeResultInFilterAsync(_resultExecutingContext.Result);
            }
            else
            {
                // The empty result is always flowed back as the 'executed' result
                _result = _result ?? new EmptyResult();

                return InvokeResultAsync(_result);
            }
        }

        private async Task<ResultExecutedContext> InvokeNextResultFilterAwaitedAsync()
        {
            Debug.Assert(_resultExecutingContext != null);
            if (_resultExecutingContext.Cancel == true)
            {
                // If we get here, it means that an async filter set cancel == true AND called next().
                // This is forbidden.
                var message = Resources.FormatAsyncResultFilter_InvalidShortCircuit(
                    typeof(IAsyncResultFilter).Name,
                    nameof(ResultExecutingContext.Cancel),
                    typeof(ResultExecutingContext).Name,
                    typeof(ResultExecutionDelegate).Name);

                throw new InvalidOperationException(message);
            }

            await InvokeNextResultFilterAsync();

            Debug.Assert(_resultExecutedContext != null);
            return _resultExecutedContext;
        }

        private async Task InvokeSyncResultFilterAsync(IResultFilter filter)
        {
            Debug.Assert(_resultExecutingContext != null);

            try
            {
                _diagnosticSource.BeforeOnResultExecuting(_resultExecutingContext, filter);
                filter.OnResultExecuting(_resultExecutingContext);
                _diagnosticSource.AfterOnResultExecuting(_resultExecutingContext, filter);

                if (_resultExecutingContext.Cancel == true)
                {
                    // Short-circuited by setting Cancel == true
                    Logger.ResourceFilterShortCircuited(filter);

                    _resultExecutedContext = new ResultExecutedContext(
                        _resultExecutingContext,
                        _filters,
                        _resultExecutingContext.Result,
                        Instance)
                    {
                        Canceled = true,
                    };
                }
                else
                {
                    await InvokeNextResultFilterAsync();

                    _diagnosticSource.BeforeOnResultExecuted(
                        _resultExecutingContext.ActionDescriptor,
                        _resultExecutedContext,
                        filter);

                    filter.OnResultExecuted(_resultExecutedContext);

                    _diagnosticSource.AfterOnResultExecuted(
                        _resultExecutingContext.ActionDescriptor,
                        _resultExecutedContext,
                        filter);
                }
            }
            catch (Exception exception)
            {
                _resultExecutedContext = new ResultExecutedContext(
                    _resultExecutingContext,
                    _filters,
                    _resultExecutingContext.Result,
                    Instance)
                {
                    ExceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception)
                };
            }
        }

        private async Task InvokeAsyncResultFilterAsync(IAsyncResultFilter filter)
        {
            Debug.Assert(_resultExecutingContext != null);

            try
            {
                _diagnosticSource.BeforeOnResultExecution(
                _resultExecutingContext,
                filter);

                await filter.OnResultExecutionAsync(_resultExecutingContext, InvokeNextResultFilterAwaitedAsync);

                _diagnosticSource.AfterOnResultExecution(
                    _resultExecutingContext.ActionDescriptor,
                    _resultExecutedContext,
                    filter);

                if (_resultExecutedContext == null || _resultExecutingContext.Cancel == true)
                {
                    // Short-circuited by not calling next || Short-circuited by setting Cancel == true
                    Logger.ResourceFilterShortCircuited(filter);

                    _resultExecutedContext = new ResultExecutedContext(
                        _resultExecutingContext,
                        _filters,
                        _resultExecutingContext.Result,
                        Instance)
                    {
                        Canceled = true,
                    };
                }
            }
            catch (Exception exception)
            {
                _resultExecutedContext = new ResultExecutedContext(
                    _resultExecutingContext,
                    _filters,
                    _resultExecutingContext.Result,
                    Instance)
                {
                    ExceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception)
                };
            }
        }

        private async Task InvokeResultInFilterAsync(IActionResult result)
        {
            // Should only be invoked if we had a result filter
            Debug.Assert(_resultExecutingContext != null);

            try
            {
                _diagnosticSource.BeforeActionResult(Context, result);

                try
                {
                    await result.ExecuteResultAsync(Context);
                }
                finally
                {
                    _diagnosticSource.AfterActionResult(Context, result);
                }

                Debug.Assert(_resultExecutedContext == null);
                _resultExecutedContext = new ResultExecutedContext(
                    _resultExecutingContext,
                    _filters,
                    _resultExecutingContext.Result,
                    Instance);
            }
            catch (Exception exception)
            {
                _resultExecutedContext = new ResultExecutedContext(
                   _resultExecutingContext,
                   _filters,
                   _resultExecutingContext.Result,
                   Instance)
                {
                    ExceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception)
                };
            }
        }

        private async Task InvokeResultAsync(IActionResult result)
        {
            _diagnosticSource.BeforeActionResult(Context, result);

            try
            {
                await result.ExecuteResultAsync(Context);
            }
            finally
            {
                _diagnosticSource.AfterActionResult(Context, result);
            }
        }

        /// <summary>
        /// A one-way cursor for filters.
        /// </summary>
        /// <remarks>
        /// This will iterate the filter collection once per-stage, and skip any filters that don't have
        /// the one of interfaces that applies to the current stage.
        ///
        /// Filters are always executed in the following order, but short circuiting plays a role.
        ///
        /// Indentation reflects nesting.
        ///
        /// 1. Exception Filters
        ///     2. Authorization Filters
        ///     3. Action Filters
        ///        Action
        ///
        /// 4. Result Filters
        ///    Result
        ///
        /// </remarks>
        private struct FilterCursor
        {
            private int _index;
            private readonly IFilterMetadata[] _filters;

            public FilterCursor(int index, IFilterMetadata[] filters)
            {
                _index = index;
                _filters = filters;
            }

            public FilterCursor(IFilterMetadata[] filters)
            {
                _index = 0;
                _filters = filters;
            }

            public void Reset()
            {
                _index = 0;
            }

            public FilterCursorItem<TFilter, TFilterAsync> GetNextFilter<TFilter, TFilterAsync>()
                where TFilter : class
                where TFilterAsync : class
            {
                // Perf: Be really careful with changes here - this method is SUPER hot. We're very careful
                // here to avoid repeated access of _index, and do things in the order that's most likely
                // to no-op.

                var index = _index;
                var length = _filters.Length;

                while (index < length)
                {
                    var filter = _filters[index++];

                    var filterAsync = filter as TFilterAsync;
                    TFilter filterSync = null;

                    if (filterAsync != null || (filterSync = filter as TFilter) != null)
                    {
                        _index = index;
                        return new FilterCursorItem<TFilter, TFilterAsync>(filterSync, filterAsync);
                    }
                }

                _index = index;
                return default(FilterCursorItem<TFilter, TFilterAsync>);
            }
        }

        private struct FilterCursorItem<TFilter, TFilterAsync>
        {
            public readonly TFilter Filter;
            public readonly TFilterAsync FilterAsync;

            public FilterCursorItem(TFilter filter, TFilterAsync filterAsync)
            {
                Filter = filter;
                FilterAsync = filterAsync;
            }
        }
    }
}
