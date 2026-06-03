namespace INTG_FIRE_PANEL_AVENAR
{
    internal class Program
    {
        [System.STAThread]
        static void Main(string[] args)
        {
            var logRepo = (log4net.Repository.Hierarchy.Hierarchy)log4net.LogManager.GetRepository();
            logRepo.Root.Level  = log4net.Core.Level.Off;
            logRepo.Configured  = true;

            new App().Run(args);
            System.Console.WriteLine("\nPresione cualquier tecla para salir...");
            System.Console.ReadKey(true);
        }
    }
}
