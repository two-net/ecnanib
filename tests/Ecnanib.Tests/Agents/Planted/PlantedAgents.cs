namespace Ecnanib.Tests.Agents.Planted;

// What AgentsRulesTests plants to show that the rules find what they look for: a component with
// operations that keep no settings, with a row that points at a model, and with rows of an agent
// that say the mode they are of, or do not. Nothing else uses it.

/// <summary>Found: a public interface of the namespace, with what starts and pauses an agent.</summary>
public interface IPlantedControls
{
    Task StartAsync(Guid agentId);

    Task PauseAsync(Guid agentId);
}

/// <summary>Found as well: every method of a public interface is an operation, also one that only reads.</summary>
public interface IPlantedReads
{
    Task ListAsync();
}

/// <summary>Not found: nothing outside the component can call it.</summary>
internal interface IPlantedInternals
{
    Task StopAsync(Guid agentId);
}

/// <summary>Not found: a class is no interface, whatever it can do.</summary>
public sealed class PlantedService
{
    public Task ResumeAsync(Guid agentId) => Task.FromResult(agentId);
}

/// <summary>Stands for a model of the catalog.</summary>
public sealed class PlantedModel
{
    public Guid Id { get; set; }
}

/// <summary>Stands for an agent.</summary>
public sealed class PlantedAgent
{
    public Guid Id { get; set; }
}

/// <summary>Stands for a role of an agent that was given a model. Its row says the mode it is of.</summary>
public sealed class PlantedRole
{
    public Guid Id { get; set; }

    public Guid ModelId { get; set; }

    public Guid AgentId { get; set; }

    public string Mode { get; set; } = "Demo";
}

/// <summary>Stands for a position of an agent. Its row says no mode.</summary>
public sealed class PlantedPosition
{
    public Guid Id { get; set; }

    public Guid AgentId { get; set; }
}

/// <summary>Stands for an order.</summary>
public sealed class PlantedOrder
{
    public Guid Id { get; set; }
}

/// <summary>Stands for a table whose name holds neither word.</summary>
public sealed class PlantedBorder
{
    public Guid Id { get; set; }
}
