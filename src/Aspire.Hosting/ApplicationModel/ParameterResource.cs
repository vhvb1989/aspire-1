// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

public sealed class ParameterResource(string name, string value) : Resource(name)
{
    public string Value { get; set; } = value;
}