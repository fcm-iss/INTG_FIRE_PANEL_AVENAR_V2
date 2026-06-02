namespace INTG_FIRE_PANEL_AVENAR
{
    internal class Program
    {
        [System.STAThread]
        static void Main(string[] args)
        {
            new App().Run(args);
            System.Console.WriteLine("\nPresione cualquier tecla para salir...");
            System.Console.ReadKey(true);
        }
    }
}
