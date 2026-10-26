using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Ecnanib.Tests.Support;

/// <summary>The Data Protection key ring in memory: the same mechanism PersistKeysToDbContext uses, without the database.</summary>
internal sealed class InMemoryXmlRepository : IXmlRepository
{
    public List<XElement> Elements { get; } = [];

    public IReadOnlyCollection<XElement> GetAllElements() => Elements.Select(e => new XElement(e)).ToList();

    public void StoreElement(XElement element, string friendlyName) => Elements.Add(new XElement(element));
}
