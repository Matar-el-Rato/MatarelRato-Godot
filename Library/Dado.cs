using System;

namespace ProyectoSoLib
{
    public class Dado
    {
        private static Random _random = new Random();

        public static (int, int) LanzarDados()
        {
            return (_random.Next(1, 7), _random.Next(1, 7));
        }
    }
}
