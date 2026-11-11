using System;

namespace DeadMiles;

/// <summary>
/// Health, hunger, thirst and stamina. Tuned to be forgiving: a full stomach lasts about an
/// hour of play, and nothing kills quickly except other people.
/// </summary>
public sealed class PlayerStats
{
    public float Health = 100, Hunger = 80, Thirst = 80, Stamina = 100;
    public bool Dead;
    private float _staminaDelay;

    /// <summary>Amount and cause, for the HUD flash.</summary>
    public event Action<float, string>? Damaged;
    public event Action<string>? Died;

    public void Tick(float dt, bool sprinting, bool moving)
    {
        if (Dead) return;
        float effort = sprinting ? 1.9f : moving ? 1.15f : 1f;
        Thirst = MathF.Max(0, Thirst - dt * 0.055f * effort);
        Hunger = MathF.Max(0, Hunger - dt * 0.028f * effort);

        if (sprinting)
        {
            Stamina = MathF.Max(0, Stamina - dt * 9f);
            _staminaDelay = 1.2f;
        }
        else if (_staminaDelay > 0) _staminaDelay -= dt;
        else
        {
            float regen = (Thirst < 20 || Hunger < 20) ? 5f : 12f;
            Stamina = MathF.Min(100, Stamina + dt * regen);
        }

        if (Thirst <= 0) Hurt(dt * 0.3f, "thirst", true);
        if (Hunger <= 0) Hurt(dt * 0.15f, "hunger", true);
        if (Hunger > 35 && Thirst > 35 && Health < 100) Health = MathF.Min(100, Health + dt * 0.3f);
    }

    public void Eat(float v) => Hunger = Math.Clamp(Hunger + v, 0, 100);
    public void Drink(float v) => Thirst = Math.Clamp(Thirst + v, 0, 100);
    public void Heal(float v) => Health = MathF.Min(100, Health + v);

    public void Hurt(float amount, string cause, bool silent = false)
    {
        if (Dead || amount <= 0) return;
        Health -= amount;
        if (!silent) Damaged?.Invoke(amount, cause);
        if (Health <= 0)
        {
            Health = 0;
            Dead = true;
            Died?.Invoke(cause);
        }
    }
}
