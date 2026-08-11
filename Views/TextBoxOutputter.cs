using System;
using System.IO;
using System.Text;
using System.Windows.Controls;

namespace SFRT_PlanningScript.Views
{
    public class TextBoxOutputter : TextWriter
    {
        readonly TextBox textBox = null;

        public TextBoxOutputter(TextBox output)
        {
            textBox = output;
        }

        public override void Write(char value)
        {
            base.Write(value);
            textBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                textBox.AppendText(value.ToString());
            }));
        }

        public override Encoding Encoding
        {
            get { return Encoding.UTF8; }
        }
    }
}
