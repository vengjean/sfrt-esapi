using Prism.Mvvm;
using System;

namespace SFRT_PlanningScript.Models
{
    public class Spacing : BindableBase
    {
        // Helper class for representing Rectagonal vs Hexagonal Spacing
        private double value_;
        public double Value
        {
            get { return value_; }
            set { SetProperty(ref value_, value); }
        }

        private string stringRep;
        public string StringRep
        {
            get { return stringRep; }
            set { SetProperty(ref stringRep, value); }
        }

        public Spacing(double rect_spacing)
        {
            value_ = rect_spacing;
            StringRep = ToString();
        }

        public override string ToString()
        {
            string v = $"{Math.Round(value_, 1)}";
            return v;
        }
    }
}