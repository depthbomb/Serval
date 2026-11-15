package dev.serval.savanna;

import com.intellij.openapi.util.TextRange;
import java.util.ArrayList;
import org.junit.Test;
import static org.junit.Assert.*;

public class SavannaBlockStructureTest {
    @Test
    public void matchesNestedTagsAndBranchesAtEndOfHeader() {
        String text = "<#if condition=\"Model.Count > 0\">\n<#if condition=\"true\">x</#if><#elseif condition=\"false\">y<#else>z</#if>";
        var structure = SavannaBlockStructure.parse(text);
        var outer = structure.markersAt(text.indexOf("\">\n") + 2);
        assertEquals(4, outer.size());
        assertEquals(0, outer.getFirst().getStartOffset());
        assertEquals(text.length(), outer.getLast().getEndOffset());
        var inner = structure.markersAt(text.indexOf("<#if", 1) + 3);
        assertEquals(2, inner.size());
        assertEquals(text.indexOf("</#if>") + 6, inner.getLast().getEndOffset());
    }

    @Test
    public void buildConditionsUnlessAndSwitchHaveIndependentMarkers() {
        String text = "<#debug><#unless condition='false'>a<#elseif condition='true'>b<#else>c</#unless></#debug>"
                + "<#release><#switch value='Model'><#case value='1'><#break if='true'>"
                + "<#default><#foreach item='x' in='Model'><#continue if='true'></#foreach><#break></#switch></#release>";
        var structure = SavannaBlockStructure.parse(text);
        assertEquals(2, structure.markersAt(text.indexOf("<#debug")).size());
        assertEquals(4, structure.markersAt(text.indexOf("<#unless")).size());
        assertEquals(2, structure.markersAt(text.indexOf("<#release")).size());
        assertEquals(4, structure.markersAt(text.indexOf("<#switch")).size());
        assertTrue(structure.markersAt(text.indexOf("<#break") + 3).isEmpty());
        assertTrue(structure.markersAt(text.indexOf("<#continue") + 3).isEmpty());
        assertEquals(2, structure.markersAt(text.indexOf("<#foreach")).size());
    }

    @Test
    public void ignoresCommentsExpressionsVerbatimAndSelfClosingCalls() {
        String text = "{# <#if condition=\"bad\"> #}{{ \"<#if>\" }}<#include view=\"X.svn\" />"
                + "<#verbatim><#if>ignored</#if></#verbatim>";
        var structure = SavannaBlockStructure.parse(text);
        assertTrue(structure.markersAt(text.indexOf("<#if>")).isEmpty());
        assertTrue(structure.markersAt(text.indexOf("<#include")).isEmpty());
        assertEquals(2, structure.markersAt(text.indexOf("<#verbatim")).size());
        assertTrue(structure.markersAt(text.lastIndexOf("<#if>") + 3).isEmpty());
    }

    @Test
    public void rejectsMismatchedAndUnclosedBlocks() {
        assertTrue(SavannaBlockStructure.parse("<#if condition=\"true\"></#foreach></#if>").markersAt(2).isEmpty());
        assertTrue(SavannaBlockStructure.parse("<#if condition=\"true\">").markersAt(2).isEmpty());
    }

    @Test
    public void standaloneDirectivesDoNotOpenBlocks() {
        String text = "<#model type=\"int\"><#if condition=\"true\"><#let name=\"label\" value=\"Model\"><#json id=\"state\" value=\"label\"><#else>none</#if>";
        var structure = SavannaBlockStructure.parse(text);
        assertTrue(structure.markersAt(text.indexOf("model")).isEmpty());
        assertTrue(structure.markersAt(text.indexOf("let")).isEmpty());
        assertTrue(structure.markersAt(text.indexOf("json")).isEmpty());
        var markers = structure.markersAt(text.indexOf("<#if") + 3);
        assertEquals(3, markers.size());
        assertEquals(text.length(), markers.getLast().getEndOffset());
    }

    @Test
    public void loopEmptyAndSlotBlocksHaveSeparatePairs() {
        String text = "<#foreach item=\"x\" in=\"Model\"><#include view=\"Card\"><#slot name=\"body\">x</#slot></#include><#empty>none</#foreach>";
        var structure = SavannaBlockStructure.parse(text);
        assertEquals(3, structure.markersAt(2).size());
        assertEquals(2, structure.markersAt(text.indexOf("<#include")).size());
        assertEquals(2, structure.markersAt(text.indexOf("<#slot")).size());
    }

    @Test
    public void rangesChooseTheInnermostCompleteBlockAtEveryOffset() {
        String first = "<#if condition='true'>first</#if>";
        String second = "<#if condition='true'>a much longer second</#if>";
        String prefix = "before<#if condition='true'>";
        String suffix = "tail</#if>after";
        String text = prefix + first + second + suffix;
        var ranges = new ArrayList<TextRange>();
        ranges.add(new TextRange(prefix.length(), prefix.length() + first.length()));
        ranges.add(new TextRange(prefix.length() + first.length(), prefix.length() + first.length() + second.length()));
        ranges.add(new TextRange("before".length(), text.length() - "after".length()));
        var structure = SavannaBlockStructure.parse(text);
        for (int offset = -1; offset <= text.length() + 1; offset++) {
            TextRange expected = TextRange.EMPTY_RANGE;
            for (var range : ranges) {
                if (range.containsOffset(offset) && (expected.isEmpty() || range.getLength() < expected.getLength())) {
                    expected = range;
                }
            }
            assertEquals("offset " + offset, expected, structure.rangeAt(offset));
        }
    }

    @Test
    public void completedChildrenStillHaveRangesInsideMalformedParents() {
        String opening = "<#if condition='true'>";
        String child = "<#for clause='var i = 0; i < 1; i++'>x</#for>";
        for (String suffix : new String[] { " tail", "</#slot> tail" }) {
            var structure = SavannaBlockStructure.parse(opening + child + suffix);
            assertEquals(new TextRange(opening.length(), opening.length() + child.length()), structure.rangeAt(opening.length() + 4));
            assertEquals(TextRange.EMPTY_RANGE, structure.rangeAt(opening.length() + child.length() + 2));
            assertEquals(TextRange.EMPTY_RANGE, structure.rangeAt(2));
        }
    }
}
