// A plain Windows text box for the end-to-end test; its title shows the text.
using System.Drawing;
using System.Windows.Forms;

static class ClassicTest
{
    [System.STAThread]
    static void Main()
    {
        var form = new Form { Text = "KBTEST[]", Width = 700, Height = 300, StartPosition = FormStartPosition.CenterScreen };
        var box = new TextBox { Dock = DockStyle.Bottom, Font = new Font("Segoe UI", 12) };
        box.TextChanged += (s, e) => form.Text = "KBTEST[" + box.Text + "]";
        form.Controls.Add(box);
        form.Shown += (s, e) => box.Focus();
        Application.Run(form);
    }
}
