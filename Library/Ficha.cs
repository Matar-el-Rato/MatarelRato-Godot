using System;

namespace ProyectoSoLib
{
    public class Ficha
    {
        private string _color; 
        private string _id; 
        private string _estado; // indica si esta en base, meta o tablero
        private Casilla _posicionActual;

        public Ficha(string color, string id)
        {
            this._color = color;
            this._id = id;
            this._estado = "base";
            this._posicionActual = new Casilla();
        }

        public string GetColor() => this._color;
        public string GetId() => this._id; 
        public string GetEstado() => this._estado;
        public void SetEstado(string estado) { this._estado = estado; }
        public Casilla GetPosicionActual() => this._posicionActual;
        public void SetPosicionActual(Casilla casilla) { this._posicionActual = casilla; }
    }
}
