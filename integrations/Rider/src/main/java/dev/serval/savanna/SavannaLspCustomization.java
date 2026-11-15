package dev.serval.savanna;

import com.intellij.platform.lsp.api.customization.LspCustomization;
import com.intellij.platform.lsp.api.customization.LspSemanticTokensSupport;
import com.intellij.psi.PsiFile;
import org.jetbrains.annotations.NotNull;

public final class SavannaLspCustomization extends LspCustomization {
    private final LspSemanticTokensSupport semanticTokens = new LspSemanticTokensSupport() {
        @Override
        public boolean shouldAskServerForSemanticTokens(@NotNull PsiFile file) {
            return file.getLanguage() == SavannaLanguage.INSTANCE;
        }
    };

    @Override
    public @NotNull LspSemanticTokensSupport getSemanticTokensCustomizer() {
        return semanticTokens;
    }
}
