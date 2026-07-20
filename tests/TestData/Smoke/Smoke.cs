namespace Smoke;

public sealed class Player
{
    public void Play()
    {
    }
}

public sealed class Caller
{
    public void Execute(Player player)
    {
        player.Play();
    }
}
