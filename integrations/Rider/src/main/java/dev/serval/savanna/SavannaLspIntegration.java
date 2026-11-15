package dev.serval.savanna;

import com.intellij.execution.configurations.GeneralCommandLine;
import com.intellij.ide.impl.TrustedProjects;
import com.intellij.ide.plugins.PluginManagerCore;
import com.intellij.openapi.extensions.PluginId;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.vfs.VirtualFile;
import com.intellij.openapi.vfs.VfsUtil;
import java.net.URI;
import java.nio.file.Path;
import com.intellij.platform.lsp.api.LspIntegrationProvider;
import com.intellij.platform.lsp.api.ProjectWideLspClientDescriptor;
import java.nio.charset.StandardCharsets;
import org.jetbrains.annotations.NotNull;

public final class SavannaLspIntegration implements LspIntegrationProvider {
    @Override
    public void fileOpened(@NotNull Project project, @NotNull VirtualFile file, @NotNull LspClientStarter starter) {
        if ("svn".equals(file.getExtension()) && TrustedProjects.isTrusted(project)) {
            starter.ensureClientStarted(new Descriptor(project));
        }
    }

    static VirtualFile findLocalFile(URI uri) {
        return VfsUtil.findFile(Path.of(uri), true);
    }

    private static final class Descriptor extends ProjectWideLspClientDescriptor {
        private final Project project;

        public Descriptor(Project project) {
            super(project, "Serval.Savanna");
            this.project = project;
        }

        @Override
        public boolean isSupportedFile(@NotNull VirtualFile file) {
            return "svn".equals(file.getExtension());
        }

        @Override
        public @NotNull SavannaLspCustomization getLspCustomization() {
            return new SavannaLspCustomization();
        }

        @Override
        public VirtualFile findFileByUri(@NotNull String fileUri) {
            try {
                var uri = URI.create(fileUri);
                if ("file".equals(uri.getScheme())) {
                    return findLocalFile(uri);
                }
            } catch (IllegalArgumentException exception) {
                return super.findFileByUri(fileUri);
            }

            return super.findFileByUri(fileUri);
        }

        @Override
        public @NotNull GeneralCommandLine createCommandLine() {
            var plugin = PluginManagerCore.getPlugin(PluginId.getId("dev.serval.savanna"));
            if (plugin == null) {
                throw new IllegalStateException("Cannot find the Serval.Savanna plugin installation.");
            }

            var server = plugin.getPluginPath().resolve("language-server/Serval.Savanna.LanguageServer.dll");
            return new GeneralCommandLine(SavannaSettings.getInstance().getDotnetPath(), server.toString())
                .withWorkDirectory(project.getBasePath()).withCharset(StandardCharsets.UTF_8);
        }
    }
}
