#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Savanna.Compiler.Html;

internal enum SavannaInterpolationContext
{
    Text,
    Attribute,
    Url,
    Forbidden,
}

internal sealed class SavannaHtmlContext
{
    public bool    IsText             => _state == State.Text;
    public bool    IsRcData           => _state == State.RawText && _rawTag is "title" or "textarea";
    public string? TextContainer      => IsRcData ? _rawTag : null;
    public string  AttributeName      { get; private set; } = "";
    public string  AttributeSpelling  { get; private set; } = "";
    public bool    IsEmptyQuotedValue => !_valueHasContent && _state is State.SingleQuoted or State.DoubleQuoted;
    public char    Quote              => _state == State.SingleQuoted ? '\'' : '"';

    public SavannaInterpolationContext Interpolation
    {
        get
        {
            if (_state == State.Text || IsRcData)
            {
                return SavannaInterpolationContext.Text;
            }

            if (_state is not (State.SingleQuoted or State.DoubleQuoted))
            {
                return SavannaInterpolationContext.Forbidden;
            }

            // HTML escaping can't make JavaScript, CSS, nested HTML, or URL lists safe to interpolate.
            if (
                AttributeName.StartsWith("on", StringComparison.Ordinal)   ||
                AttributeName is "style" or "srcdoc" or "srcset" or "ping" ||
                _tag == "meta"  && AttributeName == "content"              ||
                _tag == "param" && AttributeName == "value"                ||
                _tag is "animate" or "set" or "animatetransform" or "animatemotion")
            {
                return SavannaInterpolationContext.Forbidden;
            }

            return AttributeName is "href" or "src" or "action" or "formaction" or "poster" or "cite" or "background" or "longdesc" or "data" or "xlink:href"
                ? SavannaInterpolationContext.Url
                : SavannaInterpolationContext.Attribute;
        }
    }
    private char   _beforePrevious;
    private bool   _closing;
    private char   _previous;
    private string _rawTag = "";
    private State  _state;
    private string _tag = "";
    private bool   _valueHasContent;

    private readonly SavannaRawText _rawText = new();

    public void Read(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            switch (_state)
            {
                case State.Text:
                    if (character == '<')
                    {
                        _state   = State.Open;
                        _tag     = "";
                        _closing = false;
                    }

                    break;
                case State.Open:
                    switch (character)
                    {
                        case '/':
                            _closing = true;
                            break;
                        case '!' when index + 2 < text.Length && text[index + 1] == '-' && text[index + 2] == '-':
                            _state          =  State.Comment;
                            index           += 2;
                            _previous       =  '\0';
                            _beforePrevious =  '\0';
                            continue;
                        case var _ when char.IsLetter(character):
                            _tag   = char.ToLowerInvariant(character).ToString();
                            _state = State.Tag;
                            break;
                        default:
                            _state = State.Declaration;
                            break;
                    }

                    break;
                case State.Tag:
                    if (character == '>')
                    {
                        EndTag();
                    }
                    else if (IsWhitespace(character) || character == '/')
                    {
                        _state = State.BeforeAttribute;
                    }
                    else
                    {
                        _tag += char.ToLowerInvariant(character);
                    }

                    break;
                case State.BeforeAttribute:
                    if (character == '>')
                    {
                        EndTag();
                    }
                    else if (!IsWhitespace(character) && character != '/')
                    {
                        AttributeName     = char.ToLowerInvariant(character).ToString();
                        AttributeSpelling = character.ToString();
                        _state            = State.Attribute;
                    }

                    break;
                case State.Attribute:
                    switch (character)
                    {
                        case '=':
                        {
                            _state = State.BeforeValue;
                            break;
                        }

                        case '>':
                        {
                            EndTag();
                            break;
                        }
                        case '/' or ' ' or '\t' or '\r' or '\n' or '\f':
                        {
                            _state = State.AfterAttribute;
                            break;
                        }
                        default:
                        {
                            AttributeName     += char.ToLowerInvariant(character);
                            AttributeSpelling += character;
                            break;
                        }
                    }

                    break;
                case State.AfterAttribute:
                    switch (character)
                    {
                        case '=':
                        {
                            _state = State.BeforeValue;
                            break;
                        }

                        case '>':
                        {
                            EndTag();
                            break;
                        }
                        case not ('/' or ' ' or '\t' or '\r' or '\n' or '\f'):
                        {
                            AttributeName     = char.ToLowerInvariant(character).ToString();
                            AttributeSpelling = character.ToString();
                            _state            = State.Attribute;
                            break;
                        }
                    }

                    break;
                case State.BeforeValue:
                    switch (character)
                    {
                        case '"':
                        {
                            _state           = State.DoubleQuoted;
                            _valueHasContent = false;
                            break;
                        }

                        case '\'':
                        {
                            _state           = State.SingleQuoted;
                            _valueHasContent = false;
                            break;
                        }

                        case '>':
                        {
                            EndTag();
                            break;
                        }
                        case not (' ' or '\t' or '\r' or '\n' or '\f'):
                        {
                            _state = State.Unquoted;
                            break;
                        }
                    }

                    break;
                case State.DoubleQuoted:
                    if (character == '"')
                    {
                        _state = State.BeforeAttribute;
                    }
                    else
                    {
                        _valueHasContent = true;
                    }

                    break;
                case State.SingleQuoted:
                    if (character == '\'')
                    {
                        _state = State.BeforeAttribute;
                    }
                    else
                    {
                        _valueHasContent = true;
                    }

                    break;
                case State.Unquoted:
                    if (character == '>')
                    {
                        EndTag();
                    }
                    else if (IsWhitespace(character))
                    {
                        _state = State.BeforeAttribute;
                    }

                    break;
                case State.Comment:
                    if (character == '>' && _previous == '-' && _beforePrevious == '-')
                    {
                        _state = State.Text;
                    }

                    break;
                case State.Declaration:
                    if (character == '>')
                    {
                        _state = State.Text;
                    }

                    break;
                case State.RawText:
                    if (_rawText.IsClosingTag(text, index, _rawTag))
                    {
                        _state   = State.Open;
                        _tag     = "";
                        _closing = false;
                    }

                    break;
            }

            _beforePrevious = _previous;
            _previous       = character;
        }
    }

    private void EndTag()
    {
        var raw = !_closing && _tag is "script" or "style" or "xmp" or "iframe" or "noembed" or "noframes" or "plaintext" or "title" or "textarea";
        _state  = raw ? State.RawText : State.Text;
        _rawTag = raw ? _tag : "";
        _rawText.Reset();
    }

    private static bool IsWhitespace(char character) => character is ' ' or '\t' or '\r' or '\n' or '\f';

    private enum State
    {
        Text,
        Open,
        Tag,
        BeforeAttribute,
        Attribute,
        AfterAttribute,
        BeforeValue,
        SingleQuoted,
        DoubleQuoted,
        Unquoted,
        Comment,
        Declaration,
        RawText,
    }
}
