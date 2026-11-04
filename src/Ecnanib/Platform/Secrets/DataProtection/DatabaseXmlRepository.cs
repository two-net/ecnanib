using System.Xml.Linq;
using Ecnanib.Platform.Contracts;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Secrets.DataProtection;

/// <summary>
/// Keeps the Data Protection key ring in PostgreSQL, so a deployment is still the application, its
/// configuration file and the database (NFR-DEP-01). Synchronous, because the interface is.
/// </summary>
internal sealed class DatabaseXmlRepository(IDbContextFactory<EcnanibDbContext> contextFactory) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var context = contextFactory.CreateDbContext();

        return
        [
            .. context.Set<DataProtectionKeyRow>()
                .AsNoTracking()
                .OrderBy(row => row.Id)
                .Select(row => row.Xml)
                .AsEnumerable()
                .Select(xml => XElement.Parse(xml)),
        ];
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var context = contextFactory.CreateDbContext();

        context.Set<DataProtectionKeyRow>().Add(new DataProtectionKeyRow
        {
            FriendlyName = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
        });
        context.SaveChanges();
    }
}
