package dev.serval.savanna;

import com.intellij.openapi.util.TextRange;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.Set;
import java.util.Comparator;

final class SavannaBlockStructure {
    private static final Set<String> BLOCKS = Set.of("if", "unless", "debug", "release", "switch", "foreach", "for", "block", "include", "component", "slot", "whitespace", "verbatim");
    private final List<Block> blocks;
    private final List<Marker> markers;

    private record Marker(TextRange range, List<TextRange> related) {}

    private static final class Block {
        private final String name;
        private final Block parent;
        private final List<TextRange> markers = new ArrayList<>();
        private TextRange range;

        private Block(String name, TextRange opening, Block parent) {
            this.name = name;
            this.parent = parent;
            markers.add(opening);
        }
    }

    private SavannaBlockStructure(List<Block> blocks) {
        this.blocks = blocks;
        var indexed = new ArrayList<Marker>();
        for (var block : blocks) {
            var related = List.copyOf(block.markers);
            for (var marker : related) {
                indexed.add(new Marker(marker, related));
            }
        }
        indexed.sort(Comparator.comparingInt(marker -> marker.range.getStartOffset()));
        markers = List.copyOf(indexed);
    }

    public static SavannaBlockStructure parse(CharSequence text) {
        var lexer = new SavannaLexer();
        lexer.start(text);
        var stack = new ArrayDeque<Block>();
        var blocks = new ArrayList<Block>();
        while (lexer.getTokenType() != null) {
            if (lexer.getTokenType() == SavannaLexer.DIRECTIVE) {
                int start = lexer.getTokenStart();
                int end = lexer.getTokenEnd();
                String tag = text.subSequence(start, end).toString();
                boolean closing = tag.startsWith("</#");
                if ((closing || tag.startsWith("<#")) && tag.endsWith(">")) {
                    int nameStart = closing ? 3 : 2;
                    int nameEnd = nameStart;
                    while (nameEnd < tag.length() && Character.isLetterOrDigit(tag.charAt(nameEnd))) {
                        nameEnd++;
                    }
                    String name = tag.substring(nameStart, nameEnd);
                    var marker = new TextRange(start, end);
                    if (closing) {
                        if (!stack.isEmpty() && stack.peek().name.equals(name)) {
                            var block = stack.pop();
                            block.markers.add(marker);
                            block.range = new TextRange(block.markers.getFirst().getStartOffset(), end);
                        } else {
                            // A mismatched close must not pair unrelated blocks across malformed code.
                            stack.clear();
                        }
                    } else if ((name.equals("else") || name.equals("elseif")) && !stack.isEmpty() && Set.of("if", "unless").contains(stack.peek().name)) {
                        stack.peek().markers.add(marker);
                    } else if (name.equals("empty") && !stack.isEmpty() && Set.of("foreach", "for").contains(stack.peek().name)) {
                        stack.peek().markers.add(marker);
                    } else if ((name.equals("case") || name.equals("default")) && !stack.isEmpty() && stack.peek().name.equals("switch")) {
                        stack.peek().markers.add(marker);
                    } else if (BLOCKS.contains(name) && !tag.endsWith("/>")) {
                        var block = new Block(name, marker, stack.peek());
                        blocks.add(block);
                        stack.push(block);
                    }
                }
            }
            lexer.advance();
        }
        blocks.removeIf(block -> block.range == null);
        return new SavannaBlockStructure(List.copyOf(blocks));
    }

    public List<TextRange> markersAt(int offset) {
        int lower = 0;
        int upper = markers.size() - 1;
        while (lower <= upper) {
            int middle = (lower + upper) >>> 1;
            if (markers.get(middle).range.getStartOffset() <= offset) {
                lower = middle + 1;
            } else {
                upper = middle - 1;
            }
        }
        if (upper >= 0) {
            var marker = markers.get(upper);
            if (offset <= marker.range.getEndOffset()) {
                return marker.related;
            }
        }
        return List.of();
    }

    public TextRange rangeAt(int offset) {
        int lower = 0;
        int upper = blocks.size() - 1;
        while (lower <= upper) {
            int middle = (lower + upper) >>> 1;
            if (blocks.get(middle).range.getStartOffset() <= offset) {
                lower = middle + 1;
            } else {
                upper = middle - 1;
            }
        }

        if (upper < 0) {
            return TextRange.EMPTY_RANGE;
        }

        var block = blocks.get(upper);
        var result = containingRange(block, offset);
        // TextRange includes its end, so adjacent blocks can both contain the boundary.
        if (upper > 0 && block.range.getStartOffset() == offset) {
            var previous = containingRange(blocks.get(upper - 1), offset);
            if (!previous.isEmpty() && (result.isEmpty() || previous.getLength() <= result.getLength())) {
                result = previous;
            }
        }

        return result;
    }

    private static TextRange containingRange(Block block, int offset) {
        for (var current = block; current != null; current = current.parent) {
            if (current.range != null && current.range.containsOffset(offset)) {
                return current.range;
            }
        }

        return TextRange.EMPTY_RANGE;
    }
}
