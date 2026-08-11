using System.Windows.Media;

namespace SFRT_PlanningScript.Models
{
    public class Polygon : BaseObject
    {
        PointCollection points;
        public PointCollection Points
        {
            get { return points; }
            set { points = value; NotifyPropertyChanged(); }
        }
    };
}