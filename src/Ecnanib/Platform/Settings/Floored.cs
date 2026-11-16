namespace Ecnanib.Platform.Settings;

/// <summary>
/// A setting a user may adjust from a hard floor upward: the smallest value they may choose, and
/// the default. In the file it is a list of two numbers in that order.
/// </summary>
public readonly record struct Floored<T>(T Minimum, T Default);
