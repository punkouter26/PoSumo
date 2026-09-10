namespace PoChopAudio.Services.Dsp;

/// <summary>One particle. A struct so a burst is a single flat array with no per-particle heap traffic.</summary>
public struct Particle
{
    public float X;
    public float Y;
    public float VelocityX;
    public float VelocityY;

    /// <summary>Seconds lived so far.</summary>
    public float Age;

    /// <summary>Seconds this particle lives for. Zero or less means dead.</summary>
    public float Lifetime;

    public float Size;

    /// <summary>Rotation in radians.</summary>
    public float Rotation;

    /// <summary>Angular velocity in radians per second.</summary>
    public float RotationSpeed;

    /// <summary>Aspect ratio (height / width) of the particle.</summary>
    public float AspectRatio;

    /// <summary>Index into the caller's palette. Keeps colour out of the simulation.</summary>
    public int ColorIndex;

    public readonly bool IsAlive => Age < Lifetime;

    /// <summary>1 at birth falling to 0 at death, for fading and shrinking.</summary>
    public readonly float Remaining => Lifetime <= 0 ? 0 : Math.Clamp(1f - (Age / Lifetime), 0f, 1f);
}

/// <summary>
/// A fixed-capacity particle simulation: gravity, drag, and death by old age.
///
/// <para>
/// Pure and frame-rate independent — <see cref="Step"/> takes the elapsed seconds and touches no
/// clock, no random source it does not own, and nothing that draws. That is what makes the one
/// piece of this feature with any real logic testable, and it is why the simulation lives in
/// Services rather than inside a control.
/// </para>
/// <para>
/// Capacity is fixed at construction and <see cref="Emit"/> refuses to grow past it. In an audio
/// app the particles are decoration and the DSP is not, so the budget is enforced here rather than
/// left to whatever the UI happens to ask for.
/// </para>
/// </summary>
public sealed class ParticleField
{
    private readonly Particle[] _particles;
    private readonly Random _random;

    public ParticleField(int capacity, int? seed = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _particles = new Particle[capacity];
        _random = seed is { } value ? new Random(value) : new Random();
    }

    /// <summary>Downward acceleration, in units per second squared.</summary>
    public float Gravity { get; set; } = 900f;

    /// <summary>Velocity retained per second. 1 is frictionless; lower slows particles down.</summary>
    public float Drag { get; set; } = 0.55f;

    public int Capacity => _particles.Length;

    public int AliveCount { get; private set; }

    public bool HasLiveParticles => AliveCount > 0;

    /// <summary>The backing array. Read <see cref="AliveCount"/> entries; the rest are stale.</summary>
    public ReadOnlySpan<Particle> Alive => _particles.AsSpan(0, AliveCount);

    /// <summary>
    /// Adds up to <paramref name="count"/> particles in a cone around <paramref name="directionRadians"/>,
    /// and returns how many were actually created — fewer than asked when the field is full.
    /// </summary>
    public int Emit(
        float x,
        float y,
        int count,
        float speed = 420f,
        float directionRadians = -MathF.PI / 2,
        float spreadRadians = MathF.PI,
        float lifetimeSeconds = 1.1f,
        int paletteSize = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(paletteSize, 1);

        var created = 0;

        for (var i = 0; i < count && AliveCount < _particles.Length; i++)
        {
            var angle = directionRadians + (((float)_random.NextDouble() - 0.5f) * spreadRadians);

            // Square-rooted speed spread, so particles fill the cone rather than clumping at the
            // outer radius the way a uniform speed does.
            var magnitude = speed * (0.35f + (0.65f * MathF.Sqrt((float)_random.NextDouble())));

            _particles[AliveCount++] = new Particle
            {
                X = x,
                Y = y,
                VelocityX = MathF.Cos(angle) * magnitude,
                VelocityY = MathF.Sin(angle) * magnitude,
                Age = 0,
                Lifetime = lifetimeSeconds * (0.6f + (0.8f * (float)_random.NextDouble())),
                Size = 2.5f + (4f * (float)_random.NextDouble()),
                Rotation = (float)(_random.NextDouble() * Math.PI * 2),
                RotationSpeed = (float)((_random.NextDouble() - 0.5) * Math.PI * 6),
                AspectRatio = 1.0f + (float)(_random.NextDouble() * 1.5),
                ColorIndex = _random.Next(paletteSize),
            };

            created++;
        }

        return created;
    }

    /// <summary>
    /// Emits directional spark particles for laser slices or transient hits.
    /// </summary>
    public int EmitSparks(float x, float y, int count, float speed = 180f, int paletteSize = 4)
    {
        return Emit(
            x, y, count,
            speed: speed,
            directionRadians: 0f,
            spreadRadians: MathF.PI * 2f,
            lifetimeSeconds: 0.45f,
            paletteSize: paletteSize);
    }

    /// <summary>
    /// Advances the simulation by <paramref name="deltaSeconds"/> and compacts out the dead.
    /// </summary>
    public void Step(float deltaSeconds)
    {
        if (deltaSeconds <= 0 || AliveCount == 0)
        {
            return;
        }

        // A tab-out or a long DSP pass can hand this an enormous delta; integrating it would fling
        // every particle off screen in one frame. Clamping is more honest than skipping.
        deltaSeconds = MathF.Min(deltaSeconds, 0.1f);

        var dragFactor = MathF.Pow(Math.Clamp(Drag, 0f, 1f), deltaSeconds);
        var write = 0;

        for (var read = 0; read < AliveCount; read++)
        {
            ref var particle = ref _particles[read];

            particle.Age += deltaSeconds;

            if (!particle.IsAlive)
            {
                continue;
            }

            particle.Rotation += particle.RotationSpeed * deltaSeconds;
            particle.VelocityY += Gravity * deltaSeconds;
            particle.VelocityX *= dragFactor;
            particle.VelocityY *= dragFactor;
            particle.X += particle.VelocityX * deltaSeconds;
            particle.Y += particle.VelocityY * deltaSeconds;

            if (write != read)
            {
                _particles[write] = particle;
            }

            write++;
        }

        AliveCount = write;
    }

    public void Clear() => AliveCount = 0;
}
