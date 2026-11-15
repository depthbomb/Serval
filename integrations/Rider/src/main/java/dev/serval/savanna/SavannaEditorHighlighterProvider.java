package dev.serval.savanna;

import com.intellij.openapi.editor.colors.EditorColorsScheme;
import com.intellij.openapi.editor.ex.util.LayerDescriptor;
import com.intellij.openapi.editor.ex.util.LayeredLexerEditorHighlighter;
import com.intellij.openapi.editor.highlighter.EditorHighlighter;
import com.intellij.openapi.fileTypes.EditorHighlighterProvider;
import com.intellij.openapi.fileTypes.FileType;
import com.intellij.openapi.fileTypes.FileTypeManager;
import com.intellij.openapi.fileTypes.SyntaxHighlighterFactory;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.vfs.VirtualFile;
import org.jetbrains.annotations.NotNull;

public final class SavannaEditorHighlighterProvider implements EditorHighlighterProvider {
    @Override
    public @NotNull EditorHighlighter getEditorHighlighter(Project project, @NotNull FileType fileType,
                                                           VirtualFile file, @NotNull EditorColorsScheme colors) {
        var highlighter = new LayeredLexerEditorHighlighter(new SavannaHighlighterFactory().getSyntaxHighlighter(project, file), colors);
        var htmlType = FileTypeManager.getInstance().findFileTypeByName("HTML");
        if (htmlType != null) {
            var html = SyntaxHighlighterFactory.getSyntaxHighlighter(htmlType, project, file);
            if (html != null) {
                // One HTML layer keeps attribute and tag state across Savanna expressions.
                highlighter.registerLayer(SavannaLexer.TEXT, new LayerDescriptor(html, ""));
            }
        }

        return highlighter;
    }
}
