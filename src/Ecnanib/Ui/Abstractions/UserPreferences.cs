using Ecnanib.Database.Entities;

namespace Ecnanib.Ui.Abstractions;

/// <summary>A user's theme and animation choice (FR-UI-02, FR-UI-03); the defaults for visitors come from the <c>Ui</c> section.</summary>
public sealed record UserPreferences(UiTheme Theme, bool ReducedMotion);
