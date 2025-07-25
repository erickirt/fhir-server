﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;

namespace Microsoft.Health.Fhir.Client
{
    public interface IFhirClient
    {
        ResourceFormat Format { get; }

        HttpClient HttpClient { get; }

        Task<HttpResponseMessage> CheckExportAsync(Uri contentLocation, CancellationToken cancellationToken = default);

        Task<FhirResponse<T>> ConditionalUpdateAsync<T>(T resource, string searchCriteria, string ifMatchHeaderETag = null, string provenanceHeader = null, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<FhirResponse<T>> CreateAsync<T>(string uri, T resource, string conditionalCreateCriteria = null, string provenanceHeader = null, Dictionary<string, string> additionalHeaders = default, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<FhirResponse<T>> CreateAsync<T>(T resource, string conditionalCreateCriteria = null, string provenanceHeader = null, Dictionary<string, string> additionalHeaders = default, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<FhirResponse> DeleteAsync(string uri, CancellationToken cancellationToken = default);

        Task<FhirResponse> DeleteAsync<T>(T resource, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<Uri> ExportAsync(string path = "", string parameters = "", CancellationToken cancellationToken = default);

        Task<FhirResponse> HardDeleteAsync<T>(T resource, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<FhirResponse<T>> JsonPatchAsync<T>(T resource, string content, string ifMatchVersion = null, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<FhirResponse<T>> ConditionalJsonPatchAsync<T>(string resourceType, string searchCriteria, string content, string ifMatchVersion = null, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<FhirResponse<T>> FhirPatchAsync<T>(T resource, Parameters patchRequest, string ifMatchVersion = null, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<FhirResponse<T>> ConditionalFhirPatchAsync<T>(string resourceType, string searchCriteria, Parameters patchRequest, string ifMatchVersion = null, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<HttpResponseMessage> BulkUpdateAsync(string uri, Parameters patchRequest, CancellationToken cancellationToken = default);

        Task<FhirResponse<Bundle>> PostBundleAsync(Resource bundle, FhirBundleOptions bundleOptions = default, CancellationToken cancellationToken = default);

        Task<FhirResponse<T>> ReadAsync<T>(ResourceType resourceType, string resourceId, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<FhirResponse<T>> ReadAsync<T>(string uri, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<FhirResponse<Bundle>> SearchAsync(ResourceType resourceType, string query = null, int? count = null, CancellationToken cancellationToken = default);

        Task<FhirResponse<Bundle>> SearchAsync(string url, CancellationToken cancellationToken = default);

        Task<FhirResponse<Bundle>> SearchAsync(string url, Tuple<string, string> customHeader, CancellationToken cancellationToken = default);

        Task<FhirResponse<Bundle>> SearchPostAsync(string resourceType, string query, CancellationToken cancellationToken = default, params (string key, string value)[] body);

        Task<FhirResponse<T>> UpdateAsync<T>(string uri, T resource, string ifMatchHeaderETag = null, string provenanceHeader = null, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<FhirResponse<T>> UpdateAsync<T>(T resource, string ifMatchHeaderETag = null, string provenanceHeader = null, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<OperationOutcome> ValidateAsync(string uri, string resource, string profile = null, CancellationToken cancellationToken = default);

        Task<OperationOutcome> ValidateByIdAsync(ResourceType resourceType, string resourceId, string profile = null, CancellationToken cancellationToken = default);

        Task<FhirResponse<T>> VReadAsync<T>(ResourceType resourceType, string resourceId, string versionId, CancellationToken cancellationToken = default)
            where T : Resource;

        Task<Parameters> MemberMatch(Patient patient, Coverage coverage, CancellationToken cancellationToken = default);
    }
}
