﻿namespace Microsoft.ApplicationInsights.Web.Implementation
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Web;
    using Microsoft.ApplicationInsights.Common;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.W3C;
    using Microsoft.AspNet.TelemetryCorrelation;

#pragma warning disable 612, 618
    internal static class RequestTrackingExtensions
    {
        internal static RequestTelemetry CreateRequestTelemetryPrivate(
            this HttpContext platformContext)
        {
            if (platformContext == null)
            {
                throw new ArgumentException("platformContext");
            }

            var result = new RequestTelemetry();
            var currentActivity = Activity.Current;
            var requestContext = result.Context.Operation;

            if (currentActivity == null) 
            {
                // if there was no BeginRequest, ASP.NET HttpModule did not have a chance to set current activity (and will never do it).
                currentActivity = new Activity(ActivityHelpers.RequestActivityItemName);

                if (ActivityHelpers.IsW3CTracingEnabled)
                {
                    SetW3CContext(platformContext.Request, currentActivity);

                    // length enforced in TrySetW3CContext
                    currentActivity.SetParentId(currentActivity.GetTraceId());
                    requestContext.ParentId = currentActivity.GetParentSpanId();
                }
                else if (currentActivity.Extract(platformContext.Request.Headers))
                {
                    requestContext.ParentId = currentActivity.ParentId;
                }
                else if (ActivityHelpers.TryParseCustomHeaders(platformContext.Request, out var rootId,
                    out var parentId))
                {
                    currentActivity.SetParentId(rootId);
                    if (!string.IsNullOrEmpty(parentId))
                    {
                        requestContext.ParentId = parentId;
                    }
                }

                currentActivity.Start();
            }
            else
            {
                if (ActivityHelpers.IsW3CTracingEnabled)
                {
                    currentActivity = new Activity(ActivityHelpers.RequestActivityItemName);
                    SetW3CContext(platformContext.Request, currentActivity);
                   
                    // length enforced in TrySetW3CContext
                    currentActivity.SetParentId(currentActivity.GetTraceId());
                    currentActivity.Start();

                    requestContext.ParentId = currentActivity.GetParentSpanId();
                }
                else if (ActivityHelpers.IsHierarchicalRequestId(currentActivity.ParentId))
                {
                    requestContext.ParentId = currentActivity.ParentId;
                }
                else if (ActivityHelpers.ParentOperationIdHeaderName != null)
                {
                    var parentId =
                        platformContext.Request.UnvalidatedGetHeader(ActivityHelpers.ParentOperationIdHeaderName);
                    if (!string.IsNullOrEmpty(parentId))
                    {
                        requestContext.ParentId = parentId;
                    }
                }
            }

            // we have Activity.Current, we need to properly initialize request telemetry and store it in HttpContext
            if (string.IsNullOrEmpty(requestContext.Id))
            {
                requestContext.Id = currentActivity.RootId;
                foreach (var item in currentActivity.Baggage)
                {
                    result.Context.Properties[item.Key] = item.Value;
                }
            }

            result.Id = currentActivity.Id;

            if (ActivityHelpers.IsW3CTracingEnabled)
            {
                W3COperationCorrelationTelemetryInitializer.UpdateTelemetry(result, currentActivity, true);
            }

            // save current activity in case it will be lost - we will use it in Web.OperationCorrelationTelemetryIntitalizer
            platformContext.Items[ActivityHelpers.RequestActivityItemName] = currentActivity;

            platformContext.Items.Add(RequestTrackingConstants.RequestTelemetryItemName, result);
            WebEventSource.Log.WebTelemetryModuleRequestTelemetryCreated();

            return result;
        }
#pragma warning restore 612, 618

        internal static RequestTelemetry ReadOrCreateRequestTelemetryPrivate(
            this HttpContext platformContext)
        {
            if (platformContext == null)
            {
                throw new ArgumentException("platformContext");
            }

            var result = platformContext.GetRequestTelemetry() ??
                         CreateRequestTelemetryPrivate(platformContext);

            return result;
        }

        /// <summary>
        /// Creates request name on the base of HttpContext.
        /// </summary>
        /// <returns>Controller/Action for MVC or path for other cases.</returns>
        internal static string CreateRequestNamePrivate(this HttpContext platformContext)
        {
            var request = platformContext.Request;
            string name = request.UnvalidatedGetPath();

            if (request.RequestContext != null &&
                request.RequestContext.RouteData != null)
            {
                var routeValues = request.RequestContext.RouteData.Values;

                if (routeValues != null && routeValues.Count > 0)
                {
                    object controller;                    
                    routeValues.TryGetValue("controller", out controller);
                    string controllerString = (controller == null) ? string.Empty : controller.ToString();

                    if (!string.IsNullOrEmpty(controllerString))
                    {
                        object action;
                        routeValues.TryGetValue("action", out action);
                        string actionString = (action == null) ? string.Empty : action.ToString();

                        name = controllerString;
                        if (!string.IsNullOrEmpty(actionString))
                        {
                            name += "/" + actionString;
                        }
                        else
                        {
                            if (routeValues.Keys.Count > 1)
                            {
                                // We want to include arguments because in WebApi action is usually null 
                                // and action is resolved by controller, http method and number of arguments
                                var sortedKeys = routeValues.Keys
                                    .Where(key => !string.Equals(key, "controller", StringComparison.OrdinalIgnoreCase))
                                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                                    .ToArray();

                                string arguments = string.Join(@"/", sortedKeys);
                                name += " [" + arguments + "]";
                            }
                        }
                    }
                }
            }

            if (name.StartsWith("/__browserLink/requestData/", StringComparison.OrdinalIgnoreCase))
            {
                name = "/__browserLink";
            }

            name = request.HttpMethod + " " + name;

            return name;
        }

#pragma warning disable 612, 618
        private static void SetW3CContext(HttpRequest request, Activity activity)
        {
            var traceParent = request.UnvalidatedGetHeader(W3CConstants.TraceParentHeader);
            if (traceParent != null)
            {
                activity.SetTraceParent(StringUtilities.EnforceMaxLength(traceParent, InjectionGuardConstants.TraceParentHeaderMaxLength));
            }
            else
            {
                activity.GenerateW3CContext();
            }

            var traceState = request.UnvalidatedGetHeaders().GetNameValueCollectionFromHeader(W3CConstants.TraceStateHeader);
            if (traceState != null && traceState.Any())
            {
                string traceStateExceptAppId = string.Join(",",
                    traceState.Where(s => s.Key != W3CConstants.ApplicationIdTraceStateField).Select(kvp => kvp.Key + "=" + kvp.Value));
                activity.SetTraceState(StringUtilities.EnforceMaxLength(traceStateExceptAppId, InjectionGuardConstants.TraceStateHeaderMaxLength));
            }

            if (!activity.Baggage.Any())
            {
                var baggage = request.Headers.GetNameValueCollectionFromHeader(RequestResponseHeaders.CorrelationContextHeader);
                    
                if (baggage != null && baggage.Any())
                {
                    foreach (var item in baggage)
                    {
                        var itemName = StringUtilities.EnforceMaxLength(item.Key, InjectionGuardConstants.ContextHeaderKeyMaxLength);
                        var itemValue = StringUtilities.EnforceMaxLength(item.Value, InjectionGuardConstants.ContextHeaderValueMaxLength);
                        activity.AddBaggage(itemName, itemValue);
                    }
                }
            }
        }
#pragma warning restore 612, 618
    }
}