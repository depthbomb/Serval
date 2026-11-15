package dev.serval.savanna;

import com.intellij.codeInsight.highlighting.CodeBlockSupportHandler;
import com.intellij.openapi.util.TextRange;
import com.intellij.psi.PsiElement;
import com.intellij.psi.util.CachedValueProvider;
import com.intellij.psi.util.CachedValuesManager;
import java.util.List;
import org.jetbrains.annotations.NotNull;

public final class SavannaCodeBlockSupportHandler implements CodeBlockSupportHandler {
    @Override
    public @NotNull List<TextRange> getCodeBlockMarkerRanges(@NotNull PsiElement elementAtCursor) {
        return structure(elementAtCursor).markersAt(elementAtCursor.getTextRange().getStartOffset());
    }

    @Override
    public @NotNull TextRange getCodeBlockRange(@NotNull PsiElement elementAtCursor) {
        return structure(elementAtCursor).rangeAt(elementAtCursor.getTextRange().getStartOffset());
    }

    private static SavannaBlockStructure structure(PsiElement element) {
        var file = element.getContainingFile();
        return CachedValuesManager.getCachedValue(file,
                () -> CachedValueProvider.Result.create(SavannaBlockStructure.parse(file.getViewProvider().getContents()), file));
    }
}
