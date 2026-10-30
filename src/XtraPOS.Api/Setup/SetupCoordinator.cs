namespace XtraPOS.Api.Setup;

public sealed class SetupCoordinator
{
    private int _active;

    public bool IsConfiguring => Volatile.Read(ref _active) == 1;

    public bool TryEnter() => Interlocked.CompareExchange(ref _active, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _active, 0);
}
