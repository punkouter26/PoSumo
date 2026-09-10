namespace PoChopAudio.Services;

/// <summary>
/// Why a service call produced no value. These are domain reasons the UI turns into an error
/// message; no transport meaning is baked in.
/// </summary>
public enum OutcomeFailure
{
    /// <summary>The recording is no longer loaded, or the requested clip does not exist.</summary>
    NotFound,

    /// <summary>One or more supplied values are out of range. See <see cref="Outcome{T}.Errors"/>.</summary>
    Invalid,

    /// <summary>The payload contained no bytes.</summary>
    Empty,

    /// <summary>The payload is bigger than the feature's documented limit.</summary>
    TooLarge,

    /// <summary>The file extension is not one this build can read.</summary>
    UnsupportedMedia,

    /// <summary>The bytes were the right shape but could not be decoded or processed.</summary>
    Undecodable,

    /// <summary>No background-removal engine is available on this host.</summary>
    EngineUnavailable,
}

/// <summary>
/// The result of a service call: a value, or a reason there isn't one. Expected failures travel on
/// the normal return path rather than as exceptions — a file the user picked that will not decode
/// is an outcome to render, not an exception to catch.
/// </summary>
public sealed class Outcome<T>
{
    private static readonly IReadOnlyDictionary<string, string[]> NoErrors =
        new Dictionary<string, string[]>();

    private readonly T? _value;

    private Outcome(T? value, OutcomeFailure? failure, string message, IReadOnlyDictionary<string, string[]> errors)
    {
        _value = value;
        Failure = failure;
        Message = message;
        Errors = errors;
    }

    /// <summary>Null on success.</summary>
    public OutcomeFailure? Failure { get; }

    /// <summary>Human-readable explanation, safe to show a user. Empty on success.</summary>
    public string Message { get; }

    /// <summary>Field-keyed messages, populated only for <see cref="OutcomeFailure.Invalid"/>.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public bool IsSuccess => Failure is null;

    /// <summary>The produced value. Only valid when <see cref="IsSuccess"/>.</summary>
    /// <exception cref="InvalidOperationException">The call failed; read <see cref="Failure"/> instead.</exception>
    public T Value => Failure is null
        ? _value!
        : throw new InvalidOperationException($"The call failed with {Failure}; there is no value to read.");

    public static Outcome<T> Ok(T value) => new(value, null, string.Empty, NoErrors);

    public static Outcome<T> NotFound(string message) => Fail(OutcomeFailure.NotFound, message);

    public static Outcome<T> Invalid(IReadOnlyDictionary<string, string[]> errors) =>
        new(default, OutcomeFailure.Invalid, "One or more values are out of range.", errors);

    public static Outcome<T> Invalid(string field, string message) =>
        Invalid(new Dictionary<string, string[]> { [field] = [message] });

    public static Outcome<T> Empty(string message) => Fail(OutcomeFailure.Empty, message);

    public static Outcome<T> TooLarge(string message) => Fail(OutcomeFailure.TooLarge, message);

    public static Outcome<T> UnsupportedMedia(string message) => Fail(OutcomeFailure.UnsupportedMedia, message);

    public static Outcome<T> Undecodable(string message) => Fail(OutcomeFailure.Undecodable, message);

    public static Outcome<T> EngineUnavailable(string message) => Fail(OutcomeFailure.EngineUnavailable, message);

    private static Outcome<T> Fail(OutcomeFailure failure, string message) =>
        new(default, failure, message, NoErrors);
}
