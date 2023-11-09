// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

public class OrleansResource(string name) : Resource(name)
{
    public IResourceBuilder<AzureTableStorageResource>? ClusteringTable { get; set; }
    public IResourceBuilder<AzureBlobStorageResource>? GrainStorage { get; set; }
}