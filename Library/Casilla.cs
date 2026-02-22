using System;

namespace ProyectoSoLib
{
    public class Casilla
    {
        private int _id;
        private string _tipoCasilla; // normal, segura, pasillo o meta 
        private string _color; // gris(null), azul, verde, rojo, amarillo

        public Casilla() // constructor de casilla cuando se crea el objeto ficha (estará en la base)
        {
            this._id = 0;
            this._tipoCasilla = "base";
            this._color = null;
        }

        public Casilla(int id, string tipoCasilla, string color)
        {
            this._id = id;
            this._tipoCasilla = tipoCasilla;
            this._color = color; 
        }

        public int GetIdCasilla() => this._id;
        public string GetTipoCasilla() => this._tipoCasilla;
        public string GetColor() => this._color;

        public bool EsSegura()
        {
            return this._tipoCasilla == "Segura" || this._tipoCasilla == "Pasillo" || this._tipoCasilla == "Meta";
        }
    }
}
