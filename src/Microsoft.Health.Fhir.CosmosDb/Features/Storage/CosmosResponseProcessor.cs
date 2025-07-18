﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using MediatR;
using Microsoft.Azure.Cosmos;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Abstractions.Exceptions;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Api.Features.ExceptionNotifications;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.CosmosDb.Core.Configs;
using Microsoft.Health.Fhir.CosmosDb.Features.Metrics;
using Microsoft.Health.Fhir.CosmosDb.Features.Queries;

namespace Microsoft.Health.Fhir.CosmosDb.Features.Storage
{
    public class CosmosResponseProcessor : ICosmosResponseProcessor
    {
        private readonly RequestContextAccessor<IFhirRequestContext> _fhirRequestContextAccessor;
        private readonly CosmosDataStoreConfiguration _cosmosDataStoreConfiguration;
        private readonly IMediator _mediator;
        private readonly ICosmosQueryLogger _queryLogger;
        private readonly ILogger<CosmosResponseProcessor> _logger;

        public CosmosResponseProcessor(RequestContextAccessor<IFhirRequestContext> fhirRequestContextAccessor, IMediator mediator, CosmosDataStoreConfiguration cosmosDataStoreConfiguration, ICosmosQueryLogger queryLogger, ILogger<CosmosResponseProcessor> logger)
        {
            EnsureArg.IsNotNull(fhirRequestContextAccessor, nameof(fhirRequestContextAccessor));
            EnsureArg.IsNotNull(mediator, nameof(mediator));
            EnsureArg.IsNotNull(queryLogger, nameof(queryLogger));
            EnsureArg.IsNotNull(cosmosDataStoreConfiguration, nameof(cosmosDataStoreConfiguration));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _fhirRequestContextAccessor = fhirRequestContextAccessor;
            _mediator = mediator;
            _queryLogger = queryLogger;
            _cosmosDataStoreConfiguration = cosmosDataStoreConfiguration;
            _logger = logger;
        }

        /// <summary>
        /// Adds request charge to the response headers and throws a <see cref="RequestRateExceededException"/>
        /// if the status code is 429.
        /// </summary>
        /// <param name="response">The response that has errored</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task ProcessErrorResponseAsync(CosmosResponseMessage response, CancellationToken cancellationToken)
        {
            if (!response.IsSuccessStatusCode)
            {
                await ProcessErrorResponseAsync(response.StatusCode, response.Headers, response.ErrorMessage, cancellationToken);
            }
        }

        public async Task ProcessErrorResponseAsync(HttpStatusCode statusCode, Headers headers, string errorMessage, CancellationToken cancellationToken)
        {
            Exception exception = null;

            // Stored procedure statuses will be in the substatuscode
            if (statusCode == HttpStatusCode.TooManyRequests || headers.GetSubStatusValue() == (int)HttpStatusCode.TooManyRequests)
            {
                string retryHeader = headers["x-ms-retry-after-ms"];
                exception = new RequestRateExceededException(int.TryParse(retryHeader, out int milliseconds) ? TimeSpan.FromMilliseconds(milliseconds) : null);
            }
            else if (statusCode == HttpStatusCode.RequestTimeout)
            {
                // Cosmos DB returned 408 due to insufficient RUs or other timeout. Provide actionable message.
                _logger.LogWarning("Cosmos DB request timeout detected. This typically indicates insufficient provisioned throughput (RUs) or high load. Status: {StatusCode}, Error: {ErrorMessage}", statusCode, errorMessage);
                exception = new Microsoft.Health.Fhir.Core.Exceptions.CosmosDbRequestTimeoutException();
            }
            else if (errorMessage.Contains("Invalid Continuation Token", StringComparison.OrdinalIgnoreCase) || errorMessage.Contains("Malformed Continuation Token", StringComparison.OrdinalIgnoreCase))
            {
                exception = new Microsoft.Health.Fhir.Core.Exceptions.RequestNotValidException(Microsoft.Health.Fhir.Core.Resources.InvalidContinuationToken);
            }
            else if (statusCode == HttpStatusCode.RequestEntityTooLarge
                     || (statusCode == HttpStatusCode.BadRequest && errorMessage.Contains("Request size is too large", StringComparison.OrdinalIgnoreCase)))
            {
                // There are multiple known failures relating to RequestEntityTooLarge.
                // 1. When the document size is ~2mb (just under or at the limit) it can make it into the stored proc and fail on create
                // 2. Larger documents are rejected by CosmosDb with HttpStatusCode.RequestEntityTooLarge
                exception = new Microsoft.Health.Fhir.Core.Exceptions.RequestEntityTooLargeException();
            }
            else if (statusCode == HttpStatusCode.Forbidden)
            {
                int? subStatusValue = headers.GetSubStatusValue();
                if (subStatusValue.HasValue && Enum.IsDefined(typeof(KnownCosmosDbCmkSubStatusValue), subStatusValue))
                {
                    exception = new Microsoft.Health.Fhir.Core.Exceptions.CustomerManagedKeyException(GetCustomerManagedKeyErrorMessage(subStatusValue.Value));
                }
            }

            if (exception != null)
            {
                await EmitExceptionNotificationAsync(statusCode, exception, cancellationToken);
                throw exception;
            }
        }

        /// <summary>
        /// Updates the request context with Cosmos DB info and updates response headers with the session token and request change values.
        /// </summary>
        /// <param name="responseMessage">The response message</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task ProcessResponseAsync(CosmosResponseMessage responseMessage, CancellationToken cancellationToken)
        {
            var responseRequestCharge = responseMessage.Headers.RequestCharge;

            _queryLogger.LogQueryExecutionResult(
                responseMessage.Headers.ActivityId,
                responseMessage.Headers.RequestCharge,
                responseMessage.ContinuationToken == null ? null : "<nonempty>",
                int.TryParse(responseMessage.Headers["x-ms-item-count"], out var count) ? count : 0,
                double.TryParse(responseMessage.Headers["x-ms-request-duration-ms"], out var duration) ? duration : 0,
                responseMessage.Headers["x-ms-documentdb-partitionkeyrangeid"]);

            if (_cosmosDataStoreConfiguration.LogSdkDiagnostics)
            {
                _queryLogger.LogQueryDiagnostics(responseMessage.Headers.ActivityId, responseMessage.Diagnostics);
            }

            if (_cosmosDataStoreConfiguration.LogSdkClientElapsedTime && responseMessage.Diagnostics != null)
            {
                _queryLogger.LogQueryClientElapsedTime(responseMessage.Headers.ActivityId, responseMessage.Diagnostics.GetClientElapsedTime().ToString());
            }

            IFhirRequestContext fhirRequestContext = _fhirRequestContextAccessor.RequestContext;
            if (fhirRequestContext == null)
            {
                return;
            }

            var sessionToken = responseMessage.Headers.Session;

            if (!string.IsNullOrEmpty(sessionToken))
            {
                fhirRequestContext.ResponseHeaders[CosmosDbHeaders.SessionToken] = sessionToken;
            }

            if (fhirRequestContext.Properties.TryGetValue(Constants.CosmosDbResponseMessagesProperty, out object propertyValue))
            {
                // This is planted in FhirCosmosSearchService in order for us to relay the individual responses
                // back for analysis of the selectivity of the search.
                ((ConcurrentBag<CosmosResponseMessage>)propertyValue).Add(responseMessage);
            }

            await AddRequestChargeToFhirRequestContextAsync(responseRequestCharge, responseMessage.StatusCode, cancellationToken);
        }

        private async Task AddRequestChargeToFhirRequestContextAsync(double responseRequestCharge, HttpStatusCode? statusCode, CancellationToken cancellationToken)
        {
            IFhirRequestContext requestContext = _fhirRequestContextAccessor.RequestContext;

            lock (requestContext.ResponseHeaders)
            {
                // If there has already been a request to the database for this request, then we want to add to it.
                if (requestContext.ResponseHeaders.TryGetValue(CosmosDbHeaders.RequestCharge, out StringValues existingHeaderValue))
                {
                    if (double.TryParse(existingHeaderValue.ToString(), out double existing))
                    {
                        responseRequestCharge += existing;
                    }
                    else
                    {
                        _logger.LogWarning("Unable to parse request charge header: {Request change}", existingHeaderValue);
                    }
                }

                requestContext.ResponseHeaders[CosmosDbHeaders.RequestCharge] = responseRequestCharge.ToString(CultureInfo.InvariantCulture);
            }

            var cosmosMetrics = new CosmosStorageRequestMetricsNotification(requestContext.AuditEventType, requestContext.ResourceType)
            {
                TotalRequestCharge = responseRequestCharge,
            };

            if (statusCode.HasValue && statusCode == HttpStatusCode.TooManyRequests)
            {
                cosmosMetrics.IsThrottled = true;
            }

            try
            {
                await _mediator.Publish(cosmosMetrics, cancellationToken);
            }
            catch (ObjectDisposedException ode)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ode, "ObjectDisposedException. Unable to publish CosmosDB metric. Cancellation was requested.");
                }
                else
                {
                    _logger.LogCritical(ode, "ObjectDisposedException. Unable to publish CosmosDB metric.");
                }
            }
            catch (OperationCanceledException oce)
            {
                _logger.LogWarning(oce, "OperationCanceledException. Unable to publish CosmosDB metric. Cancellation was requested.");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Unable to publish CosmosDB metric.");
            }
        }

        private static string GetCustomerManagedKeyErrorMessage(int subStatusCode)
        {
            string errorMessage = Resources.CmkDefaultError;

            switch ((KnownCosmosDbCmkSubStatusValue)subStatusCode)
            {
                case KnownCosmosDbCmkSubStatusValue.AadClientCredentialsGrantFailure:
                    errorMessage = Resources.AadClientCredentialsGrantFailure;
                    break;
                case KnownCosmosDbCmkSubStatusValue.AadServiceUnavailable:
                    errorMessage = Resources.AadServiceUnavailable;
                    break;
                case KnownCosmosDbCmkSubStatusValue.KeyVaultAuthenticationFailure:
                    errorMessage = Resources.KeyVaultAuthenticationFailure;
                    break;
                case KnownCosmosDbCmkSubStatusValue.KeyVaultKeyNotFound:
                    errorMessage = Resources.KeyVaultKeyNotFound;
                    break;
                case KnownCosmosDbCmkSubStatusValue.KeyVaultServiceUnavailable:
                    errorMessage = Resources.KeyVaultServiceUnavailable;
                    break;
                case KnownCosmosDbCmkSubStatusValue.KeyVaultWrapUnwrapFailure:
                    errorMessage = Resources.KeyVaultWrapUnwrapFailure;
                    break;
                case KnownCosmosDbCmkSubStatusValue.InvalidKeyVaultKeyUri:
                    errorMessage = Resources.InvalidKeyVaultKeyUri;
                    break;
                case KnownCosmosDbCmkSubStatusValue.KeyVaultInternalServerError:
                    errorMessage = Resources.KeyVaultInternalServerError;
                    break;
                case KnownCosmosDbCmkSubStatusValue.KeyVaultDnsNotResolved:
                    errorMessage = Resources.KeyVaultDnsNotResolved;
                    break;
            }

            return errorMessage;
        }

        private async Task EmitExceptionNotificationAsync(HttpStatusCode statusCode, Exception exception, CancellationToken cancellationToken)
        {
            var exceptionNotification = new ExceptionNotification();

            try
            {
                IFhirRequestContext fhirRequestContext = _fhirRequestContextAccessor.RequestContext;
                var innerMostException = exception.GetBaseException();

                exceptionNotification.CorrelationId = fhirRequestContext?.CorrelationId;
                exceptionNotification.FhirOperation = fhirRequestContext?.AuditEventType;
                exceptionNotification.OuterExceptionType = exception.GetType().ToString();
                exceptionNotification.ResourceType = fhirRequestContext?.ResourceType;
                exceptionNotification.StatusCode = statusCode;
                exceptionNotification.ExceptionMessage = exception.Message;
                exceptionNotification.StackTrace = exception.StackTrace;
                exceptionNotification.InnerMostExceptionType = innerMostException.GetType().ToString();
                exceptionNotification.InnerMostExceptionMessage = innerMostException.Message;
                exceptionNotification.HResult = exception.HResult;
                exceptionNotification.Source = exception.Source;
                exceptionNotification.OuterMethod = exception.TargetSite?.Name;
                exceptionNotification.IsRequestEntityTooLarge = exception.IsRequestEntityTooLarge();
                exceptionNotification.IsRequestRateExceeded = exception.IsRequestRateExceeded();
                exceptionNotification.BaseException = exception;

                await _mediator.Publish(exceptionNotification, cancellationToken);
            }
            catch (ObjectDisposedException ode)
            {
                _logger.LogWarning(ode, "ObjectDisposedException. Failure while publishing Exception notification.");
            }
            catch (OperationCanceledException oce)
            {
                _logger.LogWarning(oce, "OperationCanceledException. Unable to publish CosmosDB metric. Cancellation was requested.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to publish CosmosDB metric.");
            }
        }
    }
}
