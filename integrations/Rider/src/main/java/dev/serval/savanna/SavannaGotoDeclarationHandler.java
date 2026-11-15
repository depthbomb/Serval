package dev.serval.savanna;

import com.intellij.codeInsight.navigation.actions.GotoDeclarationHandler;
import com.intellij.openapi.editor.Editor;
import com.intellij.openapi.fileEditor.FileDocumentManager;
import com.intellij.openapi.fileEditor.OpenFileDescriptor;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.util.TextRange;
import com.intellij.openapi.vfs.VirtualFile;
import com.intellij.platform.lsp.api.LspClient;
import com.intellij.platform.lsp.api.LspClientManager;
import com.intellij.psi.PsiElement;
import com.intellij.psi.PsiFile;
import com.intellij.psi.PsiManager;
import com.intellij.psi.impl.FakePsiElement;
import java.util.ArrayList;
import java.util.Collection;
import java.util.function.Function;
import java.util.function.BiFunction;
import org.eclipse.lsp4j.DefinitionParams;
import org.eclipse.lsp4j.Position;
import org.eclipse.lsp4j.Range;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

public final class SavannaGotoDeclarationHandler implements GotoDeclarationHandler {
    private final Function<Project, Collection<LspClient>> clients;
    private final BiFunction<Project, VirtualFile, PsiFile> psiFiles;

    public SavannaGotoDeclarationHandler() {
        this(project -> LspClientManager.getInstance(project).getClients(SavannaLspIntegration.class),
            (project, file) -> PsiManager.getInstance(project).findFile(file));
    }

    SavannaGotoDeclarationHandler(Function<Project, Collection<LspClient>> clients, BiFunction<Project, VirtualFile, PsiFile> psiFiles) {
        this.clients = clients;
        this.psiFiles = psiFiles;
    }

    @Override
    public PsiElement @Nullable [] getGotoDeclarationTargets(@Nullable PsiElement sourceElement, int offset, Editor editor) {
        var source = sourceElement == null ? null : sourceElement.getContainingFile();
        if (source == null || source.getLanguage() != SavannaLanguage.INSTANCE || source.getVirtualFile() == null) {
            return null;
        }

        var document = editor.getDocument();
        if (offset < 0 || offset > document.getTextLength()) {
            return null;
        }

        var project = source.getProject();
        int line = document.getLineNumber(offset);
        var position = new Position(line, offset - document.getLineStartOffset(line));
        var targets = new ArrayList<PsiElement>();
        for (var client : clients.apply(project)) {
            if (!client.getDescriptor().isSupportedFile(source.getVirtualFile())) {
                continue;
            }

            var parameters = new DefinitionParams(client.getDocumentIdentifier(source.getVirtualFile()), position);
            var result = client.sendRequestSync(10_000, server -> server.getTextDocumentService().definition(parameters));
            if (result == null) {
                continue;
            }

            if (result.isLeft()) {
                for (var location : result.getLeft()) {
                    addTarget(targets, project, client, location.getUri(), location.getRange());
                }
            } else {
                for (var link : result.getRight()) {
                    addTarget(targets, project, client, link.getTargetUri(), link.getTargetSelectionRange());
                }
            }
        }

        return targets.isEmpty() ? null : targets.toArray(PsiElement.EMPTY_ARRAY);
    }

    private void addTarget(ArrayList<PsiElement> targets, Project project, LspClient client, String uri, Range range) {
        var file = client.getDescriptor().findFileByUri(uri);
        if (file == null) {
            return;
        }

        var document = FileDocumentManager.getInstance().getDocument(file);
        var psiFile = psiFiles.apply(project, file);
        if (document == null || psiFile == null || range.getStart().getLine() >= document.getLineCount()) {
            return;
        }

        int offset = Math.min(document.getLineEndOffset(range.getStart().getLine()),
            document.getLineStartOffset(range.getStart().getLine()) + range.getStart().getCharacter());
        targets.add(new DeclarationTarget(psiFile, file, offset));
    }

    private static final class DeclarationTarget extends FakePsiElement {
        private final PsiFile parent;
        private final VirtualFile file;
        private final int offset;

        private DeclarationTarget(PsiFile parent, VirtualFile file, int offset) {
            this.parent = parent;
            this.file = file;
            this.offset = offset;
        }

        @Override
        public PsiElement getParent() {
            return parent;
        }

        @Override
        public String getName() {
            return file.getName();
        }

        @Override
        public int getTextOffset() {
            return offset;
        }

        @Override
        public @NotNull TextRange getTextRange() {
            return new TextRange(offset, offset);
        }

        @Override
        public boolean canNavigate() {
            return file.isValid();
        }

        @Override
        public boolean canNavigateToSource() {
            return canNavigate();
        }

        @Override
        public void navigate(boolean requestFocus) {
            new OpenFileDescriptor(parent.getProject(), file, offset).navigate(requestFocus);
        }
    }
}
