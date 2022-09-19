namespace Heliosphere.Ui;

public interface IDrawable : IDisposable {
    /// <summary>
    /// Draw this drawable.
    /// </summary>
    /// <returns>true if finished forever, false if should draw next frame</returns>
    bool Draw();
}
