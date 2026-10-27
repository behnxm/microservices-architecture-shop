using System;

namespace Shop.Contracts;

/// <summary>
/// Decorates integration event records with their stable, versioned contract routing key.
/// Example: "ordering.order-placed.v1"
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class IntegrationEventAttribute : Attribute
{
    public string ContractName { get; }

    public IntegrationEventAttribute(string contractName)
    {
        ContractName = contractName;
    }
}
