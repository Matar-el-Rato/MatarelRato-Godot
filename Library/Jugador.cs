using System;
using System.Collections.Generic;

namespace ProyectoSoLib
{
    public class Jugador
    {
        public string Id { get; set; }
        public string Color { get; set; }
        public List<Ficha> Fichas { get; private set; } = new List<Ficha>();
        public int FichasEnBase { get; set; }
        public int FichasEnMeta { get; set; }
        public Casilla CasillaSalida { get; set; }

        public Jugador(string id, string color)
        {
            Id = id;
            Color = color;
            for (int i = 0; i < 4; i++)
            {
                Fichas.Add(new Ficha(color, $"{color}_{i}"));
            }
        }
    }
}
