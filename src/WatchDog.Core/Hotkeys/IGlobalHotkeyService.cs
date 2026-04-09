namespace WatchDog.Core.Hotkeys;

public interface IGlobalHotkeyService : IDisposable
{
    bool Register(HotkeyConfig config);
    bool Unregister(int id);
    event Action<int>? HotkeyPressed;
}
