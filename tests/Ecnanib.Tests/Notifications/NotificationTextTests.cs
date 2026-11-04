using System.Resources;
using Ecnanib.Components;
using Ecnanib.Kernel;

namespace Ecnanib.Tests.Notifications;

/// <summary>
/// Every type has its words, in the app and in the email, so a type added to the Kernel fails here
/// rather than on a page or in the job. NotificationEmailTests proves the email uses them.
/// </summary>
public sealed class NotificationTextTests
{
    private static readonly NotificationType[] Types = Enum.GetValues<NotificationType>();

    [Fact]
    public void Every_type_has_its_own_text_in_the_app()
    {
        var texts = Types.Select(NotificationText.Of).ToList();

        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [Fact]
    public void The_preferences_page_groups_every_type_once()
    {
        var grouped = NotificationText.Groups.SelectMany(group => group.Types).ToList();

        Assert.Equal(Types.Order(), grouped.Order());
        Assert.All(NotificationText.Groups, group => Assert.False(string.IsNullOrWhiteSpace(group.Legend)));
    }

    /// <summary>The generated class is internal, so this reads the resource the way it does.</summary>
    [Fact]
    public void Every_type_has_its_own_title_in_the_email()
    {
        var resources = new ResourceManager("Ecnanib.Modules.Notifications.Emails", typeof(Program).Assembly);
        var titles = Types.Select(type => resources.GetString($"Title{type}")).ToList();

        Assert.All(titles, title => Assert.False(string.IsNullOrWhiteSpace(title)));
        Assert.Equal(titles.Count, titles.Distinct().Count());
    }
}
