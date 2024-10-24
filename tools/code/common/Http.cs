﻿using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public class AzureManagementRequestException(
    string message,
    HttpStatusCode statusCode,
    string errorCode,
    Uri requestUri,
    Exception? innerException)
    : HttpRequestException(message, innerException, statusCode)
{
    private const string AzureManagementRequestHost = "management.azure.com";

    private const string MethodNotAllowedInPricingTierErrorCode = "MethodNotAllowedInPricingTier";

    private const string ValidationErrorCode = "ValidationError";

    public string ErrorCode { get; } = errorCode;

    public Uri RequestUri { get; } = requestUri;

    public static AzureManagementRequestException FromResponse(Response response, Uri requestUri)
    {
        if (requestUri is not { Host: AzureManagementRequestHost })
        {
            throw new ArgumentException($"The request URI host must be '{AzureManagementRequestHost}'.", nameof(requestUri));
        }

        var errorCode = response.TryGetErrorCode()
                               .IfNone("Unknown");

        var status = (HttpStatusCode)response.Status;

        return new AzureManagementRequestException(
            message: $"The Azure Management request to URI {requestUri} failed with status code {status}. Content is '{response.Content}'.",
            statusCode: status,
            errorCode: errorCode,
            requestUri: requestUri,
            innerException: null);

    }

    public static bool IsAzureManagementRequest(Uri uri) =>
        uri is { Host: AzureManagementRequestHost };

    public bool IsMethodNotAllowedInPricingTierError =>
        ErrorCode.Equals(MethodNotAllowedInPricingTierErrorCode, StringComparison.OrdinalIgnoreCase);

    public bool IsValidationError =>
        ErrorCode.Equals(ValidationErrorCode, StringComparison.OrdinalIgnoreCase);
}

public static class HttpPipelineExtensions
{
    public static async ValueTask<BinaryData> GetContent(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        var either = await pipeline.TryGetContent(uri, cancellationToken);

        return either.IfLeftThrow(uri);
    }

    /// <summary>
    /// Gets the response content. If the status code is <see cref="HttpStatusCode.NotFound"/>, returns <see cref="Option.None"/>.
    /// </summary>
    public static async ValueTask<Option<BinaryData>> GetContentOption(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        var either = await pipeline.TryGetContent(uri, cancellationToken);

        return either.Match(response =>
        {
            using (response)
            {
                return response.Status == (int)HttpStatusCode.NotFound
                          ? Option<BinaryData>.None
                          : throw response.ToHttpRequestException(uri);
            }
        }, Option<BinaryData>.Some);
    }

    public static async ValueTask<Either<Response, BinaryData>> TryGetContent(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        using var request = pipeline.CreateRequest(uri, RequestMethod.Get);

        var response = await pipeline.SendRequestAsync(request, cancellationToken);

        if (response.IsError)
        {
            return response;
        }
        else
        {
            using (response)
            {
                return response.Content;
            }
        }
    }

    public static HttpRequestException ToHttpRequestException(this Response response, Uri requestUri)
    {
        return AzureManagementRequestException.IsAzureManagementRequest(requestUri) ?
            AzureManagementRequestException.FromResponse(response, requestUri) :
            new HttpRequestException(message: $"HTTP request to URI {requestUri} failed with status code {response.Status}. Content is '{response.Content}'.", inner: null, statusCode: (HttpStatusCode)response.Status);
    }

    public static Option<string> TryGetErrorCode(this Response response)
    {
        try
        {
            return response.Content
                .ToObjectFromJson<JsonObject>()
                .TryGetJsonObjectProperty("error")
                .Bind(error => error.TryGetStringProperty("code"))
                .ToOption();
        }
        catch (Exception exception) when (exception is ArgumentNullException or NotSupportedException or JsonException)
        {
            return Option<string>.None;
        }
    }

    public static bool HasSpecificError(this Response response, string errorCode)
    {
        try
        {
            return response.IsError && response.TryGetErrorCode()
                           .Where(code => code.Equals(errorCode, StringComparison.OrdinalIgnoreCase))
                           .IsSome;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static T IfLeftThrow<T>(this Either<Response, T> either, Uri requestUri)
    {
        return either.IfLeft(response =>
        {
            using (response)
            {
                throw response.ToHttpRequestException(requestUri);
            }
        });
    }

    public static async IAsyncEnumerable<JsonObject> ListJsonObjects(this HttpPipeline pipeline, Uri uri, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Uri? nextLink = uri;

        while (nextLink is not null)
        {
            JsonObject responseJson;
            try
            {
                responseJson = await pipeline.GetJsonObject(nextLink, cancellationToken);
            }
            catch (AzureManagementRequestException exception) when (exception.IsMethodNotAllowedInPricingTierError)
            {
                nextLink = null;
                break;
            }

            var values = responseJson.TryGetJsonArrayProperty("value")
                                     .IfLeft(() => [])
                                     .PickJsonObjects();

            foreach (var value in values)
            {
                yield return value;
            }

            nextLink = responseJson.TryGetAbsoluteUriProperty("nextLink")
                                   .ValueUnsafe();
        }
    }

    public static async ValueTask<JsonObject> GetJsonObject(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        var either = await pipeline.TryGetJsonObject(uri, cancellationToken);

        return either.IfLeftThrow(uri);
    }

    /// <summary>
    /// Gets the response content as a JSON object. If the status code is <see cref="HttpStatusCode.NotFound"/>, returns <see cref="Option.None"/>.
    /// </summary>
    public static async ValueTask<Option<JsonObject>> GetJsonObjectOption(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        var option = await pipeline.GetContentOption(uri, cancellationToken);

        return option.Map(content => content.ToObjectFromJson<JsonObject>());
    }

    public static async ValueTask<Either<Response, JsonObject>> TryGetJsonObject(this HttpPipeline pipeline, Uri uri, CancellationToken cancellationToken)
    {
        var either = await pipeline.TryGetContent(uri, cancellationToken);

        return either.Map(content => content.ToObjectFromJson<JsonObject>());
    }

    public static async ValueTask DeleteResource(this HttpPipeline pipeline, Uri uri, bool waitForCompletion, CancellationToken cancellationToken)
    {
        var either = await pipeline.TryDeleteResource(uri, waitForCompletion, cancellationToken);

        try
        {
            either.IfLeftThrow(uri);
        }
        catch (AzureManagementRequestException exception) when (!exception.IsMethodNotAllowedInPricingTierError)
        {
            throw;
        }
    }

    public static async ValueTask<Either<Response, Unit>> TryDeleteResource(this HttpPipeline pipeline, Uri uri, bool waitForCompletion, CancellationToken cancellationToken)
    {
        using var request = pipeline.CreateRequest(uri, RequestMethod.Delete);
        var response = await pipeline.SendRequestAsync(request, cancellationToken);

        if (response.IsError)
        {
            return response;
        };

        using (response)
        {
            if (waitForCompletion)
            {
                var operationResponse = await pipeline.WaitForLongRunningOperation(response, cancellationToken);
                if (operationResponse.IsError)
                {
                    return operationResponse;
                }
                else
                {
                    using (operationResponse)
                    {
                        return Unit.Default;
                    }
                }
            }
            else
            {
                return Unit.Default;
            }
        }
    }

    public static async ValueTask PutContent(this HttpPipeline pipeline, Uri uri, BinaryData content, CancellationToken cancellationToken)
    {
        var either = await pipeline.TryPutContent(uri, content, cancellationToken);

        try
        {
            either.IfLeftThrow(uri);
        }
        catch (AzureManagementRequestException exception) when (!exception.IsMethodNotAllowedInPricingTierError)
        {
            throw;
        }
    }

    public static async ValueTask<Either<Response, Unit>> TryPutContent(this HttpPipeline pipeline, Uri uri, BinaryData content, CancellationToken cancellationToken)
    {
        using var request = pipeline.CreateRequest(uri, RequestMethod.Put);
        request.Content = RequestContent.Create(content);
        request.Headers.Add("Content-type", "application/json");

        var response = await pipeline.SendRequestAsync(request, cancellationToken);
        if (response.IsError)
        {
            return response;
        };

        using (response)
        {
            var operationResponse = await pipeline.WaitForLongRunningOperation(response, cancellationToken);
            if (operationResponse.IsError)
            {
                return operationResponse;
            }

            using (operationResponse)
            {
                var verifiedResponse = await pipeline.VerifyResource(operationResponse, cancellationToken);

                using (verifiedResponse)
                {
                    return Unit.Default;
                }
            }
        }
    }

    public static async ValueTask PatchContent(this HttpPipeline pipeline, Uri uri, BinaryData content, CancellationToken cancellationToken)
    {
        var either = await pipeline.TryPatchContent(uri, content, cancellationToken);

        try
        {
            either.IfLeftThrow(uri);
        }
        catch (AzureManagementRequestException exception) when (!exception.IsMethodNotAllowedInPricingTierError)
        {
            throw;
        }
    }

    public static async ValueTask<Either<Response, Unit>> TryPatchContent(this HttpPipeline pipeline, Uri uri, BinaryData content, CancellationToken cancellationToken)
    {
        using var request = pipeline.CreateRequest(uri, RequestMethod.Patch);
        request.Content = RequestContent.Create(content);
        request.Headers.Add("Content-type", "application/json");

        var response = await pipeline.SendRequestAsync(request, cancellationToken);
        if (response.IsError)
        {
            return response;
        };

        using (response)
        {
            var operationResponse = await pipeline.WaitForLongRunningOperation(response, cancellationToken);
            if (operationResponse.IsError)
            {
                return operationResponse;
            }
            else
            {
                using (operationResponse)
                {
                    return Unit.Default;
                }
            }
        }
    }

    public static Request CreateRequest(this HttpPipeline pipeline, Uri uri, RequestMethod requestMethod)
    {
        var request = pipeline.CreateRequest();
        request.Uri.Reset(uri);
        request.Method = requestMethod;

        return request;
    }

    private static async ValueTask<Response> WaitForLongRunningOperation(this HttpPipeline pipeline, Response response, CancellationToken cancellationToken)
    {
        var updatedResponse = response;
        while ((updatedResponse.Status == ((int)HttpStatusCode.Accepted))
               && updatedResponse.Headers.TryGetValue("Location", out var locationHeaderValue)
               && Uri.TryCreate(locationHeaderValue, UriKind.Absolute, out var locationUri)
               && locationUri is not null)
        {
            if (updatedResponse.Headers.TryGetValue("Retry-After", out var retryAfterString) && int.TryParse(retryAfterString, out var retryAfterSeconds))
            {
                var retryAfterDuration = TimeSpan.FromSeconds(retryAfterSeconds);
                await Task.Delay(retryAfterDuration, cancellationToken);
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            using var request = pipeline.CreateRequest(locationUri, RequestMethod.Get);
            updatedResponse = await pipeline.SendRequestAsync(request, cancellationToken);
            if (updatedResponse.IsError)
            {
                throw updatedResponse.ToHttpRequestException(locationUri);
            }
        }

        return updatedResponse;
    }

    private static async ValueTask<Response> VerifyResource(this HttpPipeline pipeline, Response response, CancellationToken cancellationToken)
    {
        var updatedResponse = response;
        if (updatedResponse.IsError) return updatedResponse;
        if (updatedResponse.Headers.TryGetValue("Location", out var locationHeaderValue) &&
            Uri.TryCreate(locationHeaderValue, UriKind.Absolute, out var locationUri))
        {
            using var request = pipeline.CreateRequest(locationUri, RequestMethod.Get);
            updatedResponse = await pipeline.SendRequestAsync(request, cancellationToken);
            if (updatedResponse.IsError)
            {
                throw updatedResponse.ToHttpRequestException(locationUri);
            }

            if (updatedResponse.Content is null || updatedResponse.Content.IsNull())
            {
                throw new ValueIsNullException($"The resource ({locationUri.AbsolutePath}) lacks a response body indicating it has failed to deploy.");
            }
        }

        return updatedResponse;
    }
}

public sealed class ILoggerHttpPipelinePolicy(ILogger logger) : HttpPipelinePolicy
{
    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        ProcessAsync(message, pipeline).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("""
                            Starting request
                            Method: {HttpMethod}
                            Uri: {Uri}
                            Content: {RequestContent}
                            """, message.Request.Method, message.Request.Uri, await GetRequestContent(message, message.CancellationToken));
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("""
                            Starting request
                            Method: {HttpMethod}
                            Uri: {Uri}
                            """, message.Request.Method, message.Request.Uri);
        }

        var startTime = Stopwatch.GetTimestamp();

        await ProcessNextAsync(message, pipeline);

        var endTime = Stopwatch.GetTimestamp();
        var duration = TimeSpan.FromSeconds((endTime - startTime) / (double)Stopwatch.Frequency);

        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("""
                            Received response
                            Method: {HttpMethod}
                            Uri: {Uri}
                            Status code: {HttpStatusCode}
                            Duration (hh:mm:ss): {Duration}
                            Content: {ResponseContent}
                            """, message.Request.Method, message.Request.Uri, message.Response.Status, duration.ToString("c"), GetResponseContent(message));
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("""
                            Received response
                            Method: {HttpMethod}
                            Uri: {Uri}
                            Status code: {HttpStatusCode}
                            Duration (hh:mm:ss): {Duration}
                            """, message.Request.Method, message.Request.Uri, message.Response.Status, duration.ToString("c"));
        }
    }

    private static async ValueTask<string> GetRequestContent(HttpMessage message, CancellationToken cancellationToken)
    {
        if (message.Request.Content is null)
        {
            return "<null>";
        }
        else if (HeaderIsJson(message.Request.Headers))
        {
            using var stream = new MemoryStream();
            await message.Request.Content.WriteToAsync(stream, cancellationToken);
            stream.Position = 0;
            var data = await BinaryData.FromStreamAsync(stream, cancellationToken);

            return data.ToString();
        }
        else
        {
            return "<non-json>";
        }
    }

    private static bool HeaderIsJson(IEnumerable<HttpHeader> headers) =>
        headers.Any(header => header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                                && header.Value.Contains("application/json", StringComparison.OrdinalIgnoreCase));

    private static string GetResponseContent(HttpMessage message) =>
        message.Response.Content is null
        ? "<null>"
        : HeaderIsJson(message.Response.Headers)
            ? message.Response.Content.ToString()
            : "<non-json>";
}

public class CommonRetryPolicy : RetryPolicy
{
    protected override bool ShouldRetry(HttpMessage message, Exception? exception) =>
        base.ShouldRetry(message, exception) || ShouldRetryInner(message, exception);

    protected override async ValueTask<bool> ShouldRetryAsync(HttpMessage message, Exception? exception) =>
        await base.ShouldRetryAsync(message, exception) || ShouldRetryInner(message, exception);

    private static bool ShouldRetryInner(HttpMessage message, Exception? exception)
    {
        try
        {
            return
                (message, exception) switch
                {
                    ({ Response.Status: 422 or 409 }, _) when HasManagementApiRequestFailedError(message.Response) => true,
                    ({ Response.Status: 412 }, _) => true,
                    ({ Response.Status: 429 }, _) => true,
                    _ => false
                };
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasManagementApiRequestFailedError(Response response) =>
        response.HasSpecificError("ManagementApiRequestFailed");
}

public class TelemetryPolicy(Version version) : HttpPipelinePolicy
{
    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        ProcessAsync(message, pipeline).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        var header = new ProductHeaderValue("apimanagement-apiops", version.ToString());
        message.Request.Headers.Add(HttpHeader.Names.UserAgent, header.ToString());

        await ProcessNextAsync(message, pipeline);
    }
}
