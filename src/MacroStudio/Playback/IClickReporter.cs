namespace MacroStudio.Playback;

/// <summary>Implemented by whatever performs adaptive clicks, so later steps can continue from where the click really landed.</summary>
public interface IClickReporter
{
    (int X, int Y)? LastClick { get; }
}
