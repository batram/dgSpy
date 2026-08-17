public static class CodexWasHereStatusHook
{
    private const string Prefix = "Codex was here | ";

    public static void Postfix(System.Windows.Forms.ToolStripStatusLabel ___m_VMStateLabel)
    {
        string text = ___m_VMStateLabel.Text ?? string.Empty;
        if (!text.StartsWith(Prefix, System.StringComparison.Ordinal))
            ___m_VMStateLabel.Text = Prefix + text;
    }
}
