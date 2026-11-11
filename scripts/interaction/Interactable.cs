using System;
using Godot;

namespace DeadMiles;

/// <summary>Anything the player can look at and use with the interact key.</summary>
public interface IInteractable
{
    /// <summary>Prompt text, or null when there is nothing to do right now.</summary>
    string? GetPrompt(PlayerController player);
    void Interact(PlayerController player);
}

/// <summary>Anything that can be hurt by tools, bullets or bumpers.</summary>
public interface IDamageable
{
    void Damage(float amount, Node3D? source, Vector3 hitPoint);
}

/// <summary>
/// A solid world object with behaviour supplied as delegates, so props built in code do not
/// each need their own script class.
/// </summary>
public partial class Interactable : StaticBody3D, IInteractable
{
    public Func<PlayerController, string?>? PromptFn;
    public Action<PlayerController>? UseFn;

    public string? GetPrompt(PlayerController player) => PromptFn?.Invoke(player);
    public void Interact(PlayerController player) => UseFn?.Invoke(player);

    /// <summary>Walks up from a collider to the first interactable ancestor.</summary>
    public static IInteractable? Find(Node? n)
    {
        while (n != null)
        {
            if (n is IInteractable i) return i;
            n = n.GetParent();
        }
        return null;
    }

    public static IDamageable? FindDamageable(Node? n)
    {
        while (n != null)
        {
            if (n is IDamageable d) return d;
            n = n.GetParent();
        }
        return null;
    }
}
