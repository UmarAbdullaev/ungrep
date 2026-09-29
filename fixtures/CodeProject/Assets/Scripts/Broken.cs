using Game.Lab;

namespace Game
{
    // Doesn't compile: indexing still gets the type and the call that binds.
    public class Broken
    {
        public void Run(Turntable table)
        {
            table.Stop();
            table.DoesNotExist();
        }
    }
}
