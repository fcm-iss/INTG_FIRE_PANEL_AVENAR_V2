using System;

namespace INTG_FIRE_PANEL_AVENAR
{
    internal class Program
    {
        [System.STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Console.WriteLine($"[FATAL]\t\tExcepción no controlada: {e.ExceptionObject}");
                Console.WriteLine("[FATAL]\t\tPresione Enter para salir...");
                Console.ReadLine();
            };

            var logRepo = (log4net.Repository.Hierarchy.Hierarchy)log4net.LogManager.GetRepository();
            logRepo.Root.Level  = log4net.Core.Level.Off;
            logRepo.Configured  = true;

            new App().Run(args);

            Console.WriteLine("[MAIN]\t\tProceso finalizado. Presione Enter para salir...");
            Console.ReadLine();
        }
    }
}
