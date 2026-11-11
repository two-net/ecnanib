namespace Ecnanib.Platform.Settings;

/// <summary>
/// A setting a user may adjust within hard bounds: the smallest value they may choose, the
/// default, and the largest. In the file it is a list of three numbers in that order.
/// </summary>
public readonly record struct Bounded<T>(T Minimum, T Default, T Maximum);
