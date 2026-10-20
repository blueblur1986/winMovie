namespace WinMovie;

public sealed class EditHistory
{
    private readonly List<string> _states = [];
    private int _position = -1;
    public bool CanUndo => _position > 0;
    public bool CanRedo => _position < _states.Count - 1;
    public void Reset(string state) { _states.Clear(); _states.Add(state); _position = 0; }
    public void Record(string state)
    {
        if (_position >= 0 && _states[_position] == state) return;
        if (CanRedo) _states.RemoveRange(_position + 1, _states.Count - _position - 1);
        _states.Add(state); _position++;
        if (_states.Count > 100) { _states.RemoveAt(0); _position--; }
    }
    public string? Undo() => CanUndo ? _states[--_position] : null;
    public string? Redo() => CanRedo ? _states[++_position] : null;
}
