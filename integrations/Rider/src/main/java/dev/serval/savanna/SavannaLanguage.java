package dev.serval.savanna;

import com.intellij.lang.Language;

public final class SavannaLanguage extends Language {
    public static final SavannaLanguage INSTANCE = new SavannaLanguage();

    private SavannaLanguage() {
        super("Savanna");
    }
}
