namespace Heliosphere.Ui;

public interface IDrawable : IDisposable {
    /// <summary>
    /// Draw this drawable.
    /// </summary>
    /// <returns>DrawStatus to determine if the drawable is finished</returns>
    DrawStatus Draw();
}

public enum DrawStatus {
    /// <summary>
    /// The drawable is finished drawing forever. The <see cref="IDrawable.Draw"/>
    /// method will never be called again.
    /// </summary>
    Finished,

    /// <summary>
    /// The <see cref="IDrawable.Draw"/> method should continue to be called for
    /// another frame.
    /// </summary>
    Continue,
}
