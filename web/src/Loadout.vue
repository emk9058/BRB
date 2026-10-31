<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'

interface SpellChoice {
  id: number
  name: string
  icon: string
}

interface RuneChoice {
  id: number
  name: string
  icon: string
}

interface RuneRow {
  runes: RuneChoice[]
}

interface RuneStyle {
  id: number
  name: string
  icon: string
  subs: number[]
  rows: RuneRow[]
  stats: RuneRow[]
}

interface RunePage {
  id: number
  name: string
  editable: boolean
  deletable: boolean
  active: boolean
  primaryStyleId: number
  subStyleId: number
  perks: number[]
}

interface Loadout {
  spell1: number
  spell2: number
  spells: SpellChoice[]
  pages: RunePage[]
  activePageId: number | null
  styles: RuneStyle[]
}

const props = defineProps<{
  mode: 'spells' | 'runes'
  slot: number
  swiftIndex?: number | null
  swiftPerks?: number[]
  swiftPrimary?: number
  swiftSub?: number
}>()
const emit = defineEmits<{ close: []; changed: [] }>()

const loadout = ref<Loadout | null>(null)
const busy = ref(false)
const nextSecondary = ref(0)
const error = ref('')
const draft = ref<RunePage | null>(null)

const page = computed(() => props.swiftIndex == null
  ? loadout.value?.pages.find(item => item.active) ?? null
  : draft.value)
const primary = computed(() => loadout.value?.styles.find(style => style.id === page.value?.primaryStyleId) ?? null)
const secondary = computed(() => loadout.value?.styles.find(style => style.id === page.value?.subStyleId) ?? null)

async function reload() {
  const response = await fetch('/api/loadout')
  if (!response.ok) {
    error.value = 'League did not return spells or runes.'
    return
  }
  const data = await response.json() as Loadout
  loadout.value = data
  error.value = ''
  if (props.swiftIndex != null && draft.value) {
    const current = draft.value
    const match = data.pages.find(item =>
      item.primaryStyleId === current.primaryStyleId
      && item.subStyleId === current.subStyleId
      && item.perks.slice(0, 9).every((id, index) => id === current.perks[index]))
    if (match)
      draft.value = { ...current, id: match.id, name: match.name }
  }
}

function syncDraft() {
  if (props.swiftIndex == null) return
  const perks = (props.swiftPerks ?? []).slice(0, 9)
  while (perks.length < 9) perks.push(0)
  draft.value = {
    id: -1,
    name: 'Swiftplay',
    editable: true,
    deletable: false,
    active: true,
    primaryStyleId: props.swiftPrimary || 8000,
    subStyleId: props.swiftSub || 8200,
    perks,
  }
}

async function chooseSpell(spellId: number) {
  if (busy.value) return
  busy.value = true
  try {
    if (props.swiftIndex != null) {
      const response = await fetch('/api/lobby/swift', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ index: props.swiftIndex, spellSlot: props.slot, spellId }),
      })
      if (!response.ok) {
        error.value = 'That spell could not be equipped.'
        return
      }
      emit('changed')
      emit('close')
      return
    }
    const response = await fetch('/api/spells', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ slot: props.slot, spellId }),
    })
    if (!response.ok) {
      error.value = 'That spell could not be equipped.'
      return
    }
    emit('close')
  } finally {
    busy.value = false
  }
}

async function usePage(id: number) {
  if (busy.value) return
  if (props.swiftIndex != null) {
    const chosen = loadout.value?.pages.find(item => item.id === id)
    if (!chosen || !draft.value) return
    draft.value = {
      ...draft.value,
      id: chosen.id,
      name: chosen.name,
      perks: chosen.perks.slice(),
      primaryStyleId: chosen.primaryStyleId,
      subStyleId: chosen.subStyleId,
    }
    await save(chosen.perks, chosen.primaryStyleId, chosen.subStyleId)
    return
  }
  if (page.value?.id === id) return
  busy.value = true
  try {
    const response = await fetch('/api/runes/current', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id }),
    })
    if (!response.ok) {
      error.value = 'That rune page could not be selected.'
      return
    }
    nextSecondary.value = 0
    await reload()
  } finally {
    busy.value = false
  }
}

async function createPage() {
  if (busy.value) return
  busy.value = true
  try {
    const response = await fetch('/api/runes', { method: 'POST' })
    if (!response.ok) {
      error.value = 'A new rune page could not be created.'
      return
    }
    nextSecondary.value = 0
    await reload()
  } finally {
    busy.value = false
  }
}

async function deletePage() {
  if (busy.value || !page.value?.deletable) return
  busy.value = true
  try {
    const response = await fetch(`/api/runes/${page.value.id}`, { method: 'DELETE' })
    if (!response.ok) {
      error.value = 'That rune page could not be deleted.'
      return
    }
    nextSecondary.value = 0
    await reload()
  } finally {
    busy.value = false
  }
}

async function save(perks: number[], primaryStyleId?: number, subStyleId?: number) {
  if (!page.value?.editable || busy.value) return
  busy.value = true
  try {
    if (props.swiftIndex != null && draft.value) {
      const primary = primaryStyleId ?? draft.value.primaryStyleId
      const sub = subStyleId ?? draft.value.subStyleId
      const response = await fetch('/api/lobby/swift', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          index: props.swiftIndex,
          perks,
          primaryStyleId: primary,
          subStyleId: sub,
        }),
      })
      if (!response.ok) {
        error.value = 'Those runes could not be saved.'
        return
      }
      draft.value = { ...draft.value, perks: perks.slice(), primaryStyleId: primary, subStyleId: sub }
      emit('changed')
      return
    }
    const response = await fetch('/api/runes', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        id: page.value.id,
        primaryStyleId: primaryStyleId ?? page.value.primaryStyleId,
        subStyleId: subStyleId ?? page.value.subStyleId,
        perks,
      }),
    })
    if (!response.ok) {
      error.value = 'Those runes could not be saved.'
      return
    }
    await reload()
  } finally {
    busy.value = false
  }
}

function selectPrimary(style: RuneStyle) {
  if (!page.value) return
  const sub = style.subs.find(id => id !== style.id) ?? style.subs[0] ?? page.value.subStyleId
  const perks = page.value.perks.slice()
  perks.splice(0, 6, 0, 0, 0, 0, 0, 0)
  nextSecondary.value = 0
  save(perks, style.id, sub)
}

function selectPrimaryRune(row: number, id: number) {
  if (!page.value) return
  const perks = page.value.perks.slice()
  perks[row] = id
  save(perks)
}

function selectSecondary(styleId: number) {
  if (!page.value || styleId === page.value.primaryStyleId) return
  const perks = page.value.perks.slice()
  perks[4] = 0
  perks[5] = 0
  nextSecondary.value = 0
  save(perks, page.value.primaryStyleId, styleId)
}

function selectSecondaryRune(id: number) {
  if (!page.value || !secondary.value) return
  const perks = page.value.perks.slice()
  const row = secondary.value.rows.slice(1).find(slot => slot.runes.some(rune => rune.id === id))
  if (!row) return
  const other = perks[4 + nextSecondary.value]
  if (row.runes.some(rune => rune.id === other)) return
  nextSecondary.value = (nextSecondary.value + 1) % 2
  perks[4 + nextSecondary.value] = id
  save(perks)
}

function selectStat(row: number, id: number) {
  if (!page.value) return
  const perks = page.value.perks.slice()
  perks[6 + row] = id
  save(perks)
}

function selected(id: number) {
  return page.value?.perks.includes(id) ?? false
}

onMounted(() => {
  syncDraft()
  reload()
})
</script>

<template>
  <div class="sheet">
    <header>
      <h2>{{ mode === 'spells' ? 'Summoner spells' : 'Runes' }}</h2>
      <button type="button" class="text" @click="emit('close')">Close</button>
    </header>
    <p v-if="error" class="error">{{ error }}</p>

    <div v-if="mode === 'spells'" class="spell-grid">
      <button
        v-for="spell in loadout?.spells ?? []"
        :key="spell.id"
        type="button"
        class="choice"
        :class="{ on: spell.id === (slot === 1 ? loadout?.spell1 : loadout?.spell2) }"
        :disabled="busy"
        @click="chooseSpell(spell.id)"
      >
        <img :src="spell.icon" :alt="spell.name" />
        <span>{{ spell.name }}</span>
      </button>
    </div>

    <div v-else class="runes">
      <div class="pages">
        <select :value="page?.id ?? ''" :disabled="busy" @change="usePage(Number(($event.target as HTMLSelectElement).value))">
          <option v-for="item in loadout?.pages ?? []" :key="item.id" :value="item.id">{{ item.name }}</option>
        </select>
        <button v-if="swiftIndex == null" type="button" class="text" :disabled="busy" @click="createPage">New</button>
        <button v-if="swiftIndex == null" type="button" class="text" :disabled="busy || !page?.deletable" @click="deletePage">Delete</button>
      </div>
      <p v-if="page && !page.editable" class="error">This page is locked. Pick another page or make a new one.</p>
      <template v-if="page && primary">
        <h3>Primary</h3>
        <div class="trees">
          <button
            v-for="style in loadout?.styles ?? []"
            :key="'p-' + style.id"
            type="button"
            class="tree"
            :class="{ on: style.id === page.primaryStyleId }"
            :disabled="busy || !page.editable"
            @click="selectPrimary(style)"
          >
            <img :src="style.icon" :alt="style.name" />
          </button>
        </div>
        <div v-for="(row, index) in primary.rows" :key="'pr-' + index" class="row">
          <button
            v-for="rune in row.runes"
            :key="rune.id"
            type="button"
            class="rune"
            :class="{ on: page.perks[index] === rune.id, key: index === 0 }"
            :disabled="busy || !page.editable"
            @click="selectPrimaryRune(index, rune.id)"
          >
            <img :src="rune.icon" :alt="rune.name" />
            <span>{{ rune.name }}</span>
          </button>
        </div>

        <h3>Secondary</h3>
        <div class="trees">
          <button
            v-for="style in loadout?.styles ?? []"
            :key="'s-' + style.id"
            type="button"
            class="tree"
            :class="{ on: style.id === page.subStyleId }"
            :disabled="busy || !page.editable || style.id === page.primaryStyleId"
            @click="selectSecondary(style.id)"
          >
            <img :src="style.icon" :alt="style.name" />
          </button>
        </div>
        <div v-for="(row, index) in secondary?.rows.slice(1) ?? []" :key="'sr-' + index" class="row">
          <button
            v-for="rune in row.runes"
            :key="rune.id"
            type="button"
            class="rune"
            :class="{ on: selected(rune.id) }"
            :disabled="busy || !page.editable"
            @click="selectSecondaryRune(rune.id)"
          >
            <img :src="rune.icon" :alt="rune.name" />
            <span>{{ rune.name }}</span>
          </button>
        </div>

        <h3>Stats</h3>
        <div v-for="(row, index) in primary.stats" :key="'st-' + index" class="row">
          <button
            v-for="rune in row.runes"
            :key="rune.id"
            type="button"
            class="rune"
            :class="{ on: page.perks[6 + index] === rune.id }"
            :disabled="busy || !page.editable"
            @click="selectStat(index, rune.id)"
          >
            <img :src="rune.icon" :alt="rune.name" />
            <span>{{ rune.name }}</span>
          </button>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.sheet {
  position: fixed;
  inset: 0;
  z-index: 5;
  display: flex;
  flex-direction: column;
  overflow: auto;
  background: #010a13 url("/magic-background.jpg") center / cover no-repeat;
  color: #f0e6d2;
  font-family: "LoL Body", "Segoe UI", sans-serif;
}

header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: calc(0.75rem + env(safe-area-inset-top)) 0.9rem 0.75rem;
  background: rgba(0, 0, 0, 0.72);
  border-bottom: 1px solid rgba(240, 230, 210, 0.45);
}

h2, h3 {
  margin: 0;
  font-family: "LoL Display", "Times New Roman", serif;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}

h2 { font-size: 1.15rem; }
h3 { margin: 0.8rem 0.7rem 0.3rem; font-size: 0.78rem; color: #c8aa6e; }

.text, .choice, .tree, .rune {
  border: 0;
  background: transparent;
  color: #c8aa6e;
  font: inherit;
  cursor: pointer;
}

.text:disabled, .choice:disabled, .tree:disabled, .rune:disabled {
  opacity: 0.45;
  cursor: default;
}

.error {
  margin: 0.5rem 0.8rem 0;
  color: #ee241d;
  font-size: 0.85rem;
}

.spell-grid, .runes { padding-bottom: 1.2rem; }

.spell-grid, .row {
  display: flex;
  flex-wrap: wrap;
  gap: 0.45rem;
  padding: 0.7rem;
}

.choice, .rune {
  width: 4.3rem;
  color: #f0e6d2;
}

.choice img, .rune img, .tree img {
  width: 2.5rem;
  height: 2.5rem;
  border-radius: 50%;
  object-fit: cover;
  display: block;
  margin: 0 auto;
  box-shadow: inset 0 0 0 1px rgba(205, 190, 145, 0.4);
}

.rune.key img { width: 3rem; height: 3rem; }

.choice span, .rune span {
  display: block;
  margin-top: 0.15rem;
  font-size: 0.62rem;
  line-height: 1.15;
  text-align: center;
}

.choice.on img, .rune.on img, .tree.on img {
  box-shadow: 0 0 0 2px #c8aa6e, 0 0 12px rgba(200, 170, 110, 0.45);
}

.pages {
  display: flex;
  gap: 0.4rem;
  align-items: center;
  padding: 0.6rem 0.7rem 0;
}

.pages select {
  flex: 1;
  min-width: 0;
  padding: 0.5rem 0.45rem;
  border: 2px solid #695625;
  background: #1e2328;
  color: #f0e6d2;
  font: inherit;
}

.trees {
  display: flex;
  gap: 0.45rem;
  padding: 0 0.7rem;
}

.tree img { width: 2.2rem; height: 2.2rem; }
</style>
