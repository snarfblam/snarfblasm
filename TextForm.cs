using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace snarfblasm
{
    public partial class TextForm : Form
    {
        public TextForm() {
            InitializeComponent();
        }

        protected override void OnShown(EventArgs e) {
            base.OnShown(e);

            BringToFront();
        }
        public static string GetText() {
            TextForm inst = new TextForm();
            inst.ShowDialog();
            var text = inst.textBox1.Text;
            inst.Dispose();

            return text;
        }
        public static string GetText(string original) {
            TextForm inst = new TextForm();
            inst.textBox1.Text = original;
            inst.ShowDialog();
            var text = inst.textBox1.Text;
            inst.Dispose();

            return text;
        }
        
        private void button1_Click(object sender, EventArgs e) {
            Close();
        }
    }
}
