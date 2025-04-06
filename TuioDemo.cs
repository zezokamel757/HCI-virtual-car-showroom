using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.IO;
using TUIO;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

public enum AppState
{
    WAITING,
    MENU,
    OPTIONS,
    DISPLAY_360,
    DISPLAY_VIDEO
}

public class TuioDemo : Form, TuioListener
{
    // Current state.
    private AppState currentState = AppState.WAITING;

    // TUIO client and object list.
    private TuioClient client;
    private Dictionary<long, TuioObject> objectList;

    // TCP listener for external gesture messages.
    private Thread gestureListenerThread;
    private TcpListener tcpListener;
    private const int gesturePort = 9000;

    // Car selection menu.
    private string[] availableCars = { "Ford F-150", "Jeep Wrangler", "Other Car" };
    private int selectedCarIndex = 0;

    // Options menu.
    private string[] optionChoices = { "360 View", "Show Video" };
    private int selectedOptionIndex = 0;

    // 360 view images.
    private List<Image> fordFrames = new List<Image>();
    private int fordTotalFrames = 0;
    private List<Image> jeepFrames = new List<Image>();
    private int jeepTotalFrames = 0;

    // Currently selected frames for DISPLAY_360.
    private List<Image> currentFrames = null;
    private int currentTotalFrames = 0;
    private int currentFrameIndex = 0;

    // Video file path for Ford.
    private string fordVideoPath = @"C:\Users\kamel\OneDrive\Desktop\TUIO11_NET-master\bin\Debug\Introducing the New 2024 Ford® F-150® _ Ford®.mp4";
    private string jeepVideoPath = @"C:\Users\kamel\OneDrive\Desktop\TUIO11_NET-master\bin\Debug\AdWatch_ Jeep Gladiator _ Crusher.mp4";

    // Default background.
    private Image defaultBackground;

    // Fonts and brushes.
    private Font font = new Font("Arial", 14.0f);
    private SolidBrush textBrush = new SolidBrush(Color.White);
    private SolidBrush backgroundBrush = new SolidBrush(Color.DarkBlue);

    // UI refresh timer (~30 FPS).
    private System.Windows.Forms.Timer refreshTimer;

    // For relative rotation handling (MENU and OPTIONS).
    private double lastAngle = 0;
    private bool firstAngle = true;
    private double angleThreshold = 0.1; // ~5.7° in radians

    // LibVLCSharp fields for video playback.
    private LibVLC _libVLC;
    private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
    private VideoView videoView;

    public TuioDemo(int port)
    {
        // Form setup.
        this.ClientSize = new Size(800, 600);
        this.Text = "TUIO 360 Demo";
        this.DoubleBuffered = true;
        this.KeyPreview = true; // important for OnKeyDown to receive keys first

        // Ensure the form takes focus.
        this.Load += (s, e) => { this.ActiveControl = null; };
        this.Shown += (s, e) => { this.Focus(); };

        // Initialize TUIO.
        objectList = new Dictionary<long, TuioObject>();
        client = new TuioClient(port);
        client.addTuioListener(this);
        client.connect();

        // Load images.
        LoadCarImages(@"C:\Users\kamel\OneDrive\Desktop\TUIO11_NET-master\bin\Debug\CarImages", fordFrames, out fordTotalFrames);
        LoadCarImages(@"C:\Users\kamel\OneDrive\Desktop\TUIO11_NET-master\bin\Debug\jeeb", jeepFrames, out jeepTotalFrames);

        // Create default background.
        defaultBackground = new Bitmap(this.ClientSize.Width, this.ClientSize.Height);
        using (Graphics g = Graphics.FromImage(defaultBackground))
        {
            g.Clear(Color.Black);
            g.DrawString("Waiting for TUIO marker...", font, textBrush, new PointF(200, 300));
        }

        // Initialize LibVLCSharp.
        _libVLC = new LibVLC();
        _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
        videoView = new VideoView();
        videoView.Dock = DockStyle.Fill;
        videoView.Visible = false;
        videoView.MediaPlayer = _mediaPlayer;
        videoView.TabStop = false; // don't steal focus
        this.Controls.Add(videoView);

        // Start TCP listener for external gesture messages.
        StartGestureListener();

        // UI refresh timer.
        refreshTimer = new System.Windows.Forms.Timer();
        refreshTimer.Interval = 33;
        refreshTimer.Tick += (s, e) => { this.Invalidate(); };
        refreshTimer.Start();
    }

    // ----------------- helper to load images -----------------
    private void LoadCarImages(string folderPath, List<Image> frames, out int total)
    {
        frames.Clear();
        if (!Directory.Exists(folderPath))
        {
            Console.WriteLine("Folder not found: " + folderPath);
            total = 0;
            return;
        }
        string[] files = Directory.GetFiles(folderPath, "*.jpg");
        Array.Sort(files);
        foreach (string file in files)
        {
            try
            {
                Image img = Image.FromFile(file);
                frames.Add(img);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not load " + file + ": " + ex.Message);
            }
        }
        total = frames.Count;
        Console.WriteLine("Loaded " + total + " frames from " + folderPath);
    }

    // ----------------- keyboard handling -----------------
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        Console.WriteLine("KeyDown: " + e.KeyCode);

        if (e.KeyCode == Keys.Escape)
        {
            this.Close();
        }
        else if (e.KeyCode == Keys.Back)
        {
            if (currentState == AppState.DISPLAY_360 || currentState == AppState.DISPLAY_VIDEO)
            {
                if (currentState == AppState.DISPLAY_VIDEO)
                {
                    _mediaPlayer.Stop();
                    videoView.Visible = false;
                }
                currentState = AppState.OPTIONS;
                firstAngle = true;
            }
            else if (currentState == AppState.OPTIONS)
            {
                currentState = AppState.MENU;
                firstAngle = true;
            }
        }
        else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
        {
            HandleEnter();
            e.Handled = true; // prevent further processing
        }
    }

    // All former Enter logic moved here
    private void HandleEnter()
    {
        if (currentState == AppState.MENU)
        {
            Console.WriteLine("Enter: Car confirmed: " + availableCars[selectedCarIndex]);
            currentState = AppState.OPTIONS;
            firstAngle = true;
        }
        else if (currentState == AppState.OPTIONS)
        {
            Console.WriteLine("Enter: Option confirmed: " + optionChoices[selectedOptionIndex]);
            if (selectedOptionIndex == 0) // 360 View
            {
                currentState = AppState.DISPLAY_360;
                if (selectedCarIndex == 0)
                {
                    currentFrames = fordFrames;
                    currentTotalFrames = fordTotalFrames;
                }
                else if (selectedCarIndex == 1)
                {
                    currentFrames = jeepFrames;
                    currentTotalFrames = jeepTotalFrames;
                }
                else
                {
                    currentFrames = null;
                    currentTotalFrames = 0;
                }
            }
            else if (selectedOptionIndex == 1) // Show Video
            {
                string videoPath = null;

                if (selectedCarIndex == 0) // Ford
                    videoPath = fordVideoPath;
                else if (selectedCarIndex == 1) // Jeep
                    videoPath = jeepVideoPath;

                if (videoPath != null && File.Exists(videoPath))
                {
                    currentState = AppState.DISPLAY_VIDEO;
                    videoView.Visible = true;
                    var media = new Media(_libVLC, videoPath, FromType.FromPath);
                    _mediaPlayer.Play(media);
                }
                else
                {
                    MessageBox.Show("Video not available for this car.");
                }
            }

            firstAngle = true;
        }
    }

    // ----------------- painting -----------------
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.FillRectangle(backgroundBrush, new Rectangle(0, 0, this.ClientSize.Width, this.ClientSize.Height));

        if (currentState != AppState.DISPLAY_VIDEO)
            videoView.Visible = false;

        switch (currentState)
        {
            case AppState.WAITING:
                g.DrawImage(defaultBackground, 0, 0, this.ClientSize.Width, this.ClientSize.Height);
                break;

            case AppState.MENU:
                g.DrawString("Car Selection Menu", font, textBrush, new PointF(50, 50));
                for (int i = 0; i < availableCars.Length; i++)
                {
                    string prefix = (i == selectedCarIndex) ? ">> " : "   ";
                    g.DrawString(prefix + availableCars[i], font, textBrush, new PointF(50, 100 + i * 40));
                }
                g.DrawString("Rotate marker or press Enter to confirm selection.", font, textBrush, new PointF(50, 250));
                g.DrawString("Press Backspace to go back.", font, textBrush, new PointF(50, 300));
                break;

            case AppState.OPTIONS:
                g.DrawString("Options Menu", font, textBrush, new PointF(50, 50));
                for (int i = 0; i < optionChoices.Length; i++)
                {
                    string prefix = (i == selectedOptionIndex) ? ">> " : "   ";
                    g.DrawString(prefix + optionChoices[i], font, textBrush, new PointF(50, 100 + i * 40));
                }
                g.DrawString("Rotate marker or press Enter to confirm option.", font, textBrush, new PointF(50, 250));
                g.DrawString("Press Backspace to go back.", font, textBrush, new PointF(50, 300));
                break;

            case AppState.DISPLAY_360:
                if (currentFrames != null && currentTotalFrames > 0)
                {
                    g.DrawImage(currentFrames[currentFrameIndex], 0, 0, this.ClientSize.Width, this.ClientSize.Height);
                    g.DrawString($"360 View: Frame {currentFrameIndex + 1}/{currentTotalFrames}", font, textBrush, new PointF(50, 50));
                    g.DrawString("Press Backspace to return to options.", font, textBrush, new PointF(50, 100));
                }
                else
                {
                    g.DrawString("No images available for this car.", font, textBrush, new PointF(50, 50));
                }
                break;

            case AppState.DISPLAY_VIDEO:
                g.DrawString("Playing Video...", font, textBrush, new PointF(50, 50));
                g.DrawString("Press Backspace to return to options.", font, textBrush, new PointF(50, 100));
                break;
        }
    }

    // ----------------- TUIO listener methods -----------------
    public void addTuioObject(TuioObject o)
    {
        lock (objectList)
        {
            objectList[o.SessionID] = o;
        }
        if (o.SymbolID == 1 && currentState == AppState.WAITING)
        {
            currentState = AppState.MENU;
            firstAngle = true;
        }
    }

    public void updateTuioObject(TuioObject o)
    {
        if (o.SymbolID != 1) return;

        if (currentState == AppState.MENU || currentState == AppState.OPTIONS)
        {
            if (firstAngle)
            {
                lastAngle = o.Angle;
                firstAngle = false;
            }
            else
            {
                double delta = o.Angle - lastAngle;
                if (delta > Math.PI) delta -= 2 * Math.PI;
                else if (delta < -Math.PI) delta += 2 * Math.PI;
                if (Math.Abs(delta) > angleThreshold)
                {
                    if (currentState == AppState.MENU)
                    {
                        selectedCarIndex = (delta > 0) ? (selectedCarIndex + 1) % availableCars.Length : (selectedCarIndex - 1 + availableCars.Length) % availableCars.Length;
                    }
                    else
                    {
                        selectedOptionIndex = (delta > 0) ? (selectedOptionIndex + 1) % optionChoices.Length : (selectedOptionIndex - 1 + optionChoices.Length) % optionChoices.Length;
                    }
                    lastAngle = o.Angle;
                }
            }
        }
        else if (currentState == AppState.DISPLAY_360)
        {
            double angle = o.Angle;
            if (angle < 0) angle += 2 * Math.PI;
            if (currentTotalFrames > 0)
                currentFrameIndex = (int)((angle / (2 * Math.PI)) * currentTotalFrames) % currentTotalFrames;
        }
    }

    public void removeTuioObject(TuioObject o)
    {
        lock (objectList)
        {
            objectList.Remove(o.SessionID);
        }
    }

    // Unused TUIO callbacks
    public void addTuioCursor(TuioCursor c) { }
    public void updateTuioCursor(TuioCursor c) { }
    public void removeTuioCursor(TuioCursor c) { }
    public void addTuioBlob(TuioBlob b) { }
    public void updateTuioBlob(TuioBlob b) { }
    public void removeTuioBlob(TuioBlob b) { }
    public void refresh(TuioTime frameTime) { }

    // ----------------- TCP gesture listener -----------------
    private void StartGestureListener()
    {
        tcpListener = new TcpListener(IPAddress.Any, gesturePort);
        tcpListener.Start();
        gestureListenerThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    TcpClient client = tcpListener.AcceptTcpClient();
                    using (StreamReader reader = new StreamReader(client.GetStream()))
                    {
                        string message = reader.ReadLine();
                        ProcessGestureMessage(message);
                    }
                    client.Close();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Gesture listener error: " + ex.Message);
                }
            }
        });
        gestureListenerThread.IsBackground = true;
        gestureListenerThread.Start();
    }

    private void ProcessGestureMessage(string message)
    {
        this.Invoke(new Action(() =>
        {
            if (message == "CONFIRM_OK")
            {
                // Use the same handler as pressing Enter:
                HandleEnter();
            }
        }));
    }


    // ----------------- cleanup -----------------
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        client.removeTuioListener(this);
        client.disconnect();
        tcpListener.Stop();
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
        }
        if (_libVLC != null)
            _libVLC.Dispose();
        base.OnFormClosing(e);
    }

    // ----------------- entry point -----------------
    [STAThread]
    public static void Main(string[] args)
    {
        string libvlcPath = @"C:\Users\kamel\OneDrive\Desktop\TUIO11_NET-master\bin\Debug\zoza";
        Core.Initialize(libvlcPath);

        int port = 3333;
        if (args.Length == 1)
        {
            int.TryParse(args[0], out port);
            if (port == 0) port = 3333;
        }
        Application.EnableVisualStyles();
        Application.Run(new TuioDemo(port));
    }
}
