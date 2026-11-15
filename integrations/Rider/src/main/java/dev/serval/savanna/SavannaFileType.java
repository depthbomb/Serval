package dev.serval.savanna;

import com.intellij.icons.AllIcons;
import com.intellij.openapi.fileTypes.LanguageFileType;
import javax.swing.Icon;
import org.jetbrains.annotations.NotNull;

public final class SavannaFileType extends LanguageFileType {
    public static final SavannaFileType INSTANCE = new SavannaFileType();

    private SavannaFileType() {
        super(SavannaLanguage.INSTANCE);
    }

    @Override
    public @NotNull String getName() {
        return "Savanna";
    }

    @Override
    public @NotNull String getDescription() {
        return "Serval.Savanna template";
    }

    @Override
    public @NotNull String getDefaultExtension() {
        return "svn";
    }

    @Override
    public Icon getIcon() {
        return AllIcons.FileTypes.Html;
    }
}
