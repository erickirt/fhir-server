﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------
#pragma warning disable CA1028 // Enum Storage should be Int32
namespace Microsoft.Health.Fhir.Core.Features.Operations
{
    public enum QueueType : byte
    {
        Unknown = 0, // should not be used
        Export = 1,
        Import = 2,
        Defrag = 3,
        BulkDelete = 4,
        BulkUpdate = 5,
    }
}
#pragma warning restore CA1028 // Enum Storage should be Int32
