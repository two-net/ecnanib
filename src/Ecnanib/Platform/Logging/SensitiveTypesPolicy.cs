using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using Ecnanib.Kernel;
using Serilog.Core;
using Serilog.Events;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// Types that carry credentials are never taken apart by {@Value}: their headers and bodies are not
/// log content. Without the @ they render with ToString(), which the text rules cover, except for
/// SecretValue, whose ToString() is the mask.
/// </summary>
internal sealed class SensitiveTypesPolicy : IDestructuringPolicy
{
    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        [NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        result = value is HttpRequestMessage or HttpResponseMessage or HttpHeaders
            or HttpContext or HttpRequest or HttpResponse or IHeaderDictionary
            or IRequestCookieCollection or IResponseCookies
            or NetworkCredential or DbConnectionStringBuilder or SecretValue
            ? new ScalarValue(SecretRules.Mask)
            : null;

        return result is not null;
    }
}
