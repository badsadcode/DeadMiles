using Godot;

namespace DeadMiles;

/// <summary>
/// Application root. Owns the services that outlive a single game (settings, audio) and swaps
/// between the main menu and a running <see cref="GameWorld"/>.
/// </summary>
public partial class Main : Node
{
	public static Main I = null!;
	public readonly Settings Settings = new();
	public AudioDirector Audio = null!;
	private Node? _current;

	public override void _Ready()
	{
		I = this;
		ProcessMode = ProcessModeEnum.Always;
		Log.Info("Main", $"Dead Miles starting on Godot {Engine.GetVersionInfo()["string"]}");
		InputSetup.Install();
		Audio = new AudioDirector { Name = "Audio" };
		AddChild(Audio);
		Settings.Load();
		Settings.Apply(this);

		var harness = DebugHarness.TryCreate();
		if (harness != null) AddChild(harness);
		else ShowMenu();
	}

	private void Swap(Node next)
	{
		// Deferred: this is usually called from a button inside the scene being replaced.
		Callable.From(() =>
		{
			if (_current != null)
			{
				RemoveChild(_current);
				_current.QueueFree();
			}
			_current = next;
			AddChild(next);
		}).CallDeferred();
	}

	public void ShowMenu()
	{
		GetTree().Paused = false;
		Swap(new MainMenu { Name = "MainMenu" });
	}

	public void StartNewGame(int seed)
	{
		GetTree().Paused = false;
		Swap(new GameWorld { Name = "Game", State = new SaveData { Seed = seed, TimeOfDay = 13.5f }, IsNewGame = true });
	}

	/// <summary>New game on a real map (see tools/osm_import), starting near the given world position.</summary>
	public void StartNewMapGame(string map, float startX, float startZ)
	{
		GetTree().Paused = false;
		var state = new SaveData { Seed = MapWorld.TerrainSeed, Map = map, MapStart = new[] { startX, startZ }, TimeOfDay = 13.5f };
		Swap(new GameWorld { Name = "Game", State = state, IsNewGame = true });
	}

	public void ContinueGame()
	{
		GetTree().Paused = false;
		var data = SaveService.Load();
		if (data == null)
		{
			Log.Warn("Main", "No usable save to continue from");
			ShowMenu();
			return;
		}
		Swap(new GameWorld { Name = "Game", State = data });
	}
}
