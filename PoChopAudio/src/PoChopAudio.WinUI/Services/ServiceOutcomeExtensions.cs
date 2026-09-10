using PoChopAudio.Services;

namespace PoChopAudio.WinUI.Services;

/// <summary>Raised when a service call the view model treated as routine did not produce a value.</summary>
public sealed class ServiceFailureException(OutcomeFailure failure, string message)
    : Exception(message)
{
    public OutcomeFailure Failure { get; } = failure;
}

public static class ServiceOutcomeExtensions
{
    /// <summary>
    /// Unwraps an outcome inside a view model's existing try/catch. The service keeps expected
    /// failures off the exception path; this converts them at the consumer boundary, where the
    /// view models already funnel everything into an ErrorMessage. The message the user sees is
    /// the service's own wording rather than an HTTP status line.
    /// </summary>
    public static T OrThrow<T>(this Outcome<T> outcome) =>
        outcome.IsSuccess
            ? outcome.Value
            : throw new ServiceFailureException(outcome.Failure!.Value, outcome.Message);
}
