using System.Text.Json;

static class InputProbe
{
    public static void Run(string resultPath)
    {
        var thread = new Thread(() => {
            System.Windows.Forms.Application.EnableVisualStyles();
            using var form = new System.Windows.Forms.Form { Text = "iControl input test", Width = 620, Height = 340,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(10, 10),
                BackColor = System.Drawing.Color.LightGreen, KeyPreview = true };
            var button = new System.Windows.Forms.Button { Text = "Mouse test", Left = 30, Top = 35, Width = 180, Height = 65 };
            var input = new System.Windows.Forms.TextBox { Left = 30, Top = 135, Width = 440 };
            var label = new System.Windows.Forms.Label { Text = "Clicks: 0", Left = 240, Top = 55, Width = 150 };
            int clicks = 0;
            var keys = new List<string>();
            void Save() => File.WriteAllText(resultPath, JsonSerializer.Serialize(new { checkedAt = DateTimeOffset.Now,
                clicks, text = input.Text, keys }));
            button.Click += (_, _) => { clicks++; label.Text = "Clicks: " + clicks; Save(); };
            input.TextChanged += (_, _) => Save();
            form.KeyDown += (_, e) => { keys.Add(e.KeyCode.ToString()); Save(); };
            form.Controls.AddRange([button, input, label]);
            Save();
            System.Windows.Forms.Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
    }
}
